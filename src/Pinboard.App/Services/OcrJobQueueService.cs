using System.Security.Cryptography;
using System.Text.Json;

namespace Pinboard.App.Services;

public sealed record OcrQueueFailure(string DocumentPath, string AssetHash, string Error, int Attempts, DateTimeOffset RetryAt);

/// <summary>
/// Original screenshot bytes live on disk until OCR has committed. A single
/// worker loads only its current image, so a capture burst cannot retain dozens
/// of uncompressed screenshots in tasks waiting for the OCR semaphore.
/// </summary>
public sealed class OcrJobQueueService : IAsyncDisposable
{
    private readonly IOcrRecognizer _ocr;
    private readonly Func<string, PinboardDocument> _resolveDocument;
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;

    public OcrJobQueueService(IOcrRecognizer ocr, Func<string, PinboardDocument> resolveDocument, string? queueDirectory = null)
    {
        _ocr = ocr;
        _resolveDocument = resolveDocument;
        QueueDirectory = Path.GetFullPath(queueDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pinboard", "OcrQueue"));
    }

    public string QueueDirectory { get; }
    public event EventHandler<OcrQueueFailure>? JobFailed;

    public void Start()
    {
        if (_worker is { IsCompleted: false })
        {
            return;
        }
        Directory.CreateDirectory(QueueDirectory);
        _workerCancellation?.Dispose();
        _workerCancellation = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_workerCancellation.Token));
        Signal();
    }

    public async Task EnqueueAsync(
        string documentPath,
        string assetHash,
        byte[] originalBytes,
        double x,
        double y,
        bool chinese,
        bool english,
        CancellationToken cancellationToken = default)
    {
        if (!chinese && !english)
        {
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetHash);
        ArgumentNullException.ThrowIfNull(originalBytes);
        if (originalBytes.Length == 0)
        {
            throw new ArgumentException("OCR input cannot be empty.", nameof(originalBytes));
        }
        var fullPath = Path.GetFullPath(documentPath);
        var identity = $"{fullPath.ToUpperInvariant()}\n{assetHash.ToUpperInvariant()}\n{chinese}:{english}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        await _queueGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(QueueDirectory);
            var metadataPath = GetMetadataPath(id);
            if (!File.Exists(metadataPath))
            {
                // The metadata rename is the commit marker. A crash before it
                // leaves at most an unreferenced payload, never an empty OCR job.
                await WriteDurablyAsync(GetPayloadPath(id), originalBytes, cancellationToken);
                var now = DateTimeOffset.UtcNow;
                await WriteJobAsync(new OcrJob(id, fullPath, assetHash, x, y, chinese, english,
                    now, now, 0, null), cancellationToken);
            }
        }
        finally
        {
            _queueGate.Release();
        }
        Signal();
    }

    public async Task StopAsync()
    {
        _workerCancellation?.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // An interrupted job stays on disk and resumes next startup.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _workerCancellation?.Dispose();
        _queueGate.Dispose();
        _signal.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            OcrJob? next;
            try
            {
                next = await FindNextJobAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                PublishFailure(new OcrQueueFailure(string.Empty, string.Empty, exception.Message, 0,
                    DateTimeOffset.UtcNow.AddMinutes(1)));
                await _signal.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
                continue;
            }
            if (next is null || next.RetryAt > DateTimeOffset.UtcNow)
            {
                var delay = next is null ? TimeSpan.FromHours(1) : next.RetryAt - DateTimeOffset.UtcNow;
                await _signal.WaitAsync(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
                continue;
            }
            try
            {
                await ProcessJobAsync(next, cancellationToken);
                await _queueGate.WaitAsync(cancellationToken);
                try
                {
                    File.Delete(GetMetadataPath(next.Id));
                    File.Delete(GetPayloadPath(next.Id));
                }
                finally
                {
                    _queueGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var attempts = next.Attempts + 1;
                var retryAt = DateTimeOffset.UtcNow.Add(GetRetryDelay(attempts));
                var failed = next with { Attempts = attempts, RetryAt = retryAt, LastError = exception.Message };
                try
                {
                    await _queueGate.WaitAsync(cancellationToken);
                    try
                    {
                        await WriteJobAsync(failed, cancellationToken);
                    }
                    finally
                    {
                        _queueGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // The existing job is recoverable even when retry metadata
                    // cannot be updated (for example a full disk).
                    await _signal.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
                }
                PublishFailure(new OcrQueueFailure(next.DocumentPath, next.AssetHash, exception.Message, attempts, retryAt));
            }
        }
    }

    private async Task ProcessJobAsync(OcrJob job, CancellationToken cancellationToken)
    {
        if (!File.Exists(job.DocumentPath))
        {
            throw new FileNotFoundException("OCR is waiting for the original board; it will not recreate a deleted board.", job.DocumentPath);
        }
        var document = _resolveDocument(job.DocumentPath);
        var existing = await document.LoadOcrResultsAsync(job.AssetHash, cancellationToken);
        var chinese = job.Chinese && !HasCompleteLanguage(existing, "zh");
        var english = job.English && !HasCompleteLanguage(existing, "en");
        if (!chinese && !english)
        {
            return;
        }
        var bytes = await File.ReadAllBytesAsync(GetPayloadPath(job.Id), cancellationToken);
        var results = await _ocr.RecognizeAsync(bytes, chinese, english, cancellationToken);
        foreach (var result in results)
        {
            await document.SaveOcrAsync(job.AssetHash, result.Language, result.Text, result.Status,
                result.Error, job.X, job.Y, cancellationToken);
        }
        var completed = await document.LoadOcrResultsAsync(job.AssetHash, cancellationToken);
        if ((chinese && !HasCompleteLanguage(completed, "zh")) || (english && !HasCompleteLanguage(completed, "en")))
        {
            throw new InvalidOperationException(string.Join("; ", results
                .Where(result => result.Status != "complete")
                .Select(result => result.Error ?? $"OCR {result.Language}: {result.Status}")) is { Length: > 0 } error
                ? error : "OCR did not complete all requested languages.");
        }
    }

    private async Task<OcrJob?> FindNextJobAsync(CancellationToken cancellationToken)
    {
        await _queueGate.WaitAsync(cancellationToken);
        try
        {
            OcrJob? next = null;
            foreach (var path in Directory.EnumerateFiles(QueueDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stream = File.OpenRead(path);
                    var candidate = await JsonSerializer.DeserializeAsync<OcrJob>(stream, cancellationToken: cancellationToken);
                    if (candidate is null || candidate.Version != 1 || candidate.Id is not { Length: 64 } ||
                        !candidate.Id.All(Uri.IsHexDigit) ||
                        string.IsNullOrWhiteSpace(candidate.DocumentPath) || string.IsNullOrWhiteSpace(candidate.AssetHash) ||
                        !Path.GetFileNameWithoutExtension(path).Equals(candidate.Id, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The OCR job metadata is invalid.");
                    }
                    if (next is null || candidate.RetryAt < next.RetryAt ||
                        (candidate.RetryAt == next.RetryAt && candidate.CreatedAt < next.CreatedAt))
                    {
                        next = candidate;
                    }
                }
                catch (JsonException)
                {
                    File.Move(path, path + ".invalid", overwrite: true);
                }
                catch (InvalidDataException)
                {
                    File.Move(path, path + ".invalid", overwrite: true);
                }
            }
            return next;
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private Task WriteJobAsync(OcrJob job, CancellationToken cancellationToken) =>
        WriteDurablyAsync(GetMetadataPath(job.Id), JsonSerializer.SerializeToUtf8Bytes(job), cancellationToken);

    private static async Task WriteDurablyAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static TimeSpan GetRetryDelay(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));

    private static bool HasCompleteLanguage(IEnumerable<OcrTextResult> results, string prefix) =>
        results.Any(result => result.Status == "complete" &&
            (result.Language.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
             result.Language.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)));

    private string GetMetadataPath(string id) => Path.Combine(QueueDirectory, id + ".json");
    private string GetPayloadPath(string id) => Path.Combine(QueueDirectory, id + ".image");

    private void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void PublishFailure(OcrQueueFailure failure)
    {
        try { JobFailed?.Invoke(this, failure); }
        catch { /* A notification handler must not terminate the OCR worker. */ }
    }

    private sealed record OcrJob(
        string Id, string DocumentPath, string AssetHash, double X, double Y, bool Chinese, bool English,
        DateTimeOffset CreatedAt, DateTimeOffset RetryAt, int Attempts, string? LastError)
    {
        public int Version { get; init; } = 1;
    }
}
