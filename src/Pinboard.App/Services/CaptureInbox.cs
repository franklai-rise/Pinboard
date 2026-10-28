using System.Text;
using System.Text.Json;

namespace Pinboard.App.Services;

public sealed record PendingCapture(
    string Id, string Kind, string TargetPath, DateTimeOffset CapturedAt,
    string Source, uint ClipboardSequence, int Attempts = 0, DateTimeOffset? RetryAfter = null);

/// <summary>
/// Journals each clipboard event before processing. The worker holds one payload;
/// other pending events stay on disk, and their IDs make database replay idempotent.
/// </summary>
public sealed class CaptureInbox : IAsyncDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    public event EventHandler<Exception>? ProcessingFailed;

    public CaptureInbox(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public void Start(Func<PendingCapture, CancellationToken, Task> process)
    {
        if (_worker is not null) return;
        ArgumentNullException.ThrowIfNull(process);
        _worker = Task.Run(() => WorkAsync(process, _stop.Token));
        Wake();
    }

    public Task<PendingCapture> EnqueueTextAsync(string targetPath, string text,
        DateTimeOffset capturedAt, uint sequence = 0, CancellationToken cancellationToken = default) =>
        EnqueueAsync("text", targetPath, Encoding.UTF8.GetBytes(text), capturedAt, "clipboard", sequence, cancellationToken);

    public Task<PendingCapture> EnqueueImageAsync(string targetPath, byte[] bytes,
        DateTimeOffset capturedAt, uint sequence = 0, CancellationToken cancellationToken = default) =>
        EnqueueAsync("image", targetPath, bytes, capturedAt, "pixpin", sequence, cancellationToken);

    private async Task<PendingCapture> EnqueueAsync(string kind, string targetPath, byte[] payload,
        DateTimeOffset capturedAt, string source, uint sequence, CancellationToken cancellationToken)
    {
        var job = new PendingCapture($"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}",
            kind, Path.GetFullPath(targetPath), capturedAt, source, sequence);
        await WriteAtomicAsync(PayloadPath(job.Id), payload, cancellationToken).ConfigureAwait(false);
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        Wake();
        return job;
    }

    public Task<byte[]> ReadImageAsync(PendingCapture job, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(PayloadPath(job.Id), cancellationToken);

    public Task<string> ReadTextAsync(PendingCapture job, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(PayloadPath(job.Id), Encoding.UTF8, cancellationToken);

    public IReadOnlyList<PendingCapture> ReadPending()
    {
        _journalGate.Wait();
        try
        {
            var pending = new List<PendingCapture>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var job = JsonSerializer.Deserialize<PendingCapture>(File.ReadAllText(path));
                    if (job is not null && IsValidId(job.Id) && job.Kind is "text" or "image"
                        && Path.IsPathFullyQualified(job.TargetPath)
                        && Path.GetFullPath(path).Equals(JobPath(job.Id), StringComparison.OrdinalIgnoreCase)
                        && File.Exists(PayloadPath(job.Id))) pending.Add(job);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    LogFailure("capture-inbox-read", exception);
                }
            }
            return pending.OrderBy(job => job.CapturedAt).ThenBy(job => job.Id, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            _journalGate.Release();
        }
    }

    private async Task WorkAsync(Func<PendingCapture, CancellationToken, Task> process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                IReadOnlyList<PendingCapture> pending;
                try
                {
                    pending = ReadPending();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    LogFailure("capture-inbox-scan", exception);
                    PublishFailure(exception);
                    await _signal.WaitAsync(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                    continue;
                }
                var metadataWriteFailed = false;
                foreach (var job in pending)
                {
                    token.ThrowIfCancellationRequested();
                    if (job.RetryAfter > DateTimeOffset.UtcNow) continue;
                    try
                    {
                        await process(job, token).ConfigureAwait(false);
                        // Remove the receipt first. A crash before payload deletion
                        // leaves only an unused payload, never a duplicate capture.
                        // Once the processor has committed successfully, complete
                        // acknowledgement even when shutdown is requested now.
                        await _journalGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            File.Delete(JobPath(job.Id));
                            try { File.Delete(PayloadPath(job.Id)); }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                // The capture is already committed and acknowledged;
                                // an orphan cleanup error must not replay processing.
                                LogFailure("capture-inbox-cleanup", exception);
                            }
                        }
                        finally { _journalGate.Release(); }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        LogFailure("capture-inbox-process", exception);
                        var attempts = job.Attempts + 1;
                        try
                        {
                            await WriteJobAsync(job with
                            {
                                Attempts = attempts,
                                RetryAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(attempts, 6))))
                            }, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception metadataException)
                        {
                            // Retain the last committed receipt and payload. A full
                            // or unavailable disk cannot terminate the serial worker.
                            LogFailure("capture-inbox-retry-write", metadataException);
                            metadataWriteFailed = true;
                        }
                        PublishFailure(exception);
                    }
                }
                await _signal.WaitAsync(metadataWriteFailed ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _signal.Dispose();
        _journalGate.Dispose();
    }

    private async Task WriteJobAsync(PendingCapture job, CancellationToken token)
    {
        await _journalGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(JobPath(job.Id), JsonSerializer.SerializeToUtf8Bytes(job), token).ConfigureAwait(false);
        }
        finally { _journalGate.Release(); }
    }

    private static async Task WriteAtomicAsync(string destination, byte[] bytes, CancellationToken token)
    {
        var temporary = destination + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string JobPath(string id) => Path.Combine(_directory, id + ".json");
    private string PayloadPath(string id) => Path.Combine(_directory, id + ".payload");
    private static bool IsValidId(string? id) => id is { Length: 52 }
        && id[19] == '-' && id[..19].All(char.IsDigit) && Guid.TryParseExact(id[20..], "N", out _);
    private void Wake() { try { _signal.Release(); } catch (SemaphoreFullException) { } }

    private void PublishFailure(Exception exception)
    {
        try { ProcessingFailed?.Invoke(this, exception); }
        catch (Exception notificationException) { LogFailure("capture-inbox-notify", notificationException); }
    }

    private static void LogFailure(string operation, Exception exception) =>
        DiagnosticLog.Write(operation, new IOException($"{exception.GetType().Name} (0x{exception.HResult:X8})"));
}
