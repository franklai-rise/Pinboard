using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Pinboard.App.Interop;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed class ClipboardCaptureService : IDisposable
{
    private static readonly object InternalTextLock = new();
    private static string? _internalText;
    private static uint _internalTextSequence;
    private static DateTimeOffset _internalTextExpiresAt;
    private HwndSource? _source;
    private uint _armedSequence;
    private uint _lastTextSequence;
    private CancellationTokenSource? _armCancellation;
    private CancellationTokenSource? _textRetryCancellation;
    private uint _textRetrySequence;
    private long _armGeneration;
    private bool _processing;
    private bool _disposed;
    private ClipboardTextPrivacyFilter _textPrivacyFilter = new(AppSettings.CreateDefaultExcludedApplications());

    public event EventHandler<byte[]>? ImageCaptured;
    public event EventHandler<string>? TextCaptured;
    public event EventHandler? CaptureTimedOut;

    public bool IsArmed => _armCancellation is { IsCancellationRequested: false };
    public bool TextCaptureEnabled { get; set; }
    public bool TextCapturePaused { get; set; }
    public bool TextPrivacyModeEnabled { get; set; } = true;
    public DateTimeOffset LastImageCapturedAt { get; private set; }
    public DateTimeOffset LastTextCapturedAt { get; private set; }
    public uint LastImageSequence { get; private set; }
    public uint LastTextSequence { get; private set; }

    public void ConfigureTextCapture(AppSettings settings)
    {
        TextCaptureEnabled = settings.TextCaptureEnabled;
        TextCapturePaused = settings.TextCapturePaused;
        TextPrivacyModeEnabled = settings.TextPrivacyModeEnabled;
        _textPrivacyFilter = new ClipboardTextPrivacyFilter(settings.TextCaptureExcludedApplications);
        if (!CanCaptureText) CancelTextRetry();
    }

    /// <summary>
    /// Copies application-owned text without adding it to the automatic Text Clips board.
    /// This prevents commands such as “Copy file path” from collecting themselves.
    /// </summary>
    public static void SetTextWithoutCapture(string text)
    {
        lock (InternalTextLock)
        {
            _internalText = NormalizeText(text);
            _internalTextSequence = 0;
            _internalTextExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
        }

        try
        {
            Clipboard.SetText(text);
            lock (InternalTextLock)
            {
                _internalTextSequence = NativeMethods.GetClipboardSequenceNumber();
            }
        }
        catch
        {
            lock (InternalTextLock)
            {
                _internalText = null;
                _internalTextSequence = 0;
                _internalTextExpiresAt = default;
            }
            throw;
        }
    }

    public void Attach(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException(LocalizationService.T("ClipboardConnectFailed"));
        _source.AddHook(WindowProc);
        if (!NativeMethods.AddClipboardFormatListener(windowHandle))
        {
            throw new InvalidOperationException(LocalizationService.T("ClipboardListenFailed"));
        }
    }

    public void Arm(TimeSpan timeout)
    {
        Disarm();
        CancelTextRetry();
        _armedSequence = NativeMethods.GetClipboardSequenceNumber();
        var generation = _armGeneration;
        var cancellation = new CancellationTokenSource(timeout);
        _armCancellation = cancellation;
        cancellation.Token.Register(() =>
        {
            var dispatcher = _source?.Dispatcher ?? Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(() =>
            {
                if (_disposed || generation != _armGeneration || !ReferenceEquals(_armCancellation, cancellation)) return;
                Disarm();
                CaptureTimedOut?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    public void Disarm()
    {
        _armGeneration++;
        _armCancellation?.Cancel();
        _armCancellation?.Dispose();
        _armCancellation = null;
        _processing = false;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || message != NativeMethods.WmClipboardUpdate)
        {
            return IntPtr.Zero;
        }

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        var capturedAt = DateTimeOffset.Now;
        var armed = IsArmed;
        if (armed && !_processing && sequence != 0 && sequence != _armedSequence)
        {
            _processing = true;
            _ = TryConsumeImageAsync(sequence, capturedAt, _armGeneration);
        }

        // An armed PixPin capture owns the next clipboard change. Do not treat any
        // temporary text format it publishes as a normal text clip.
        if (!armed)
        {
            QueueTextCapture(sequence, capturedAt);
        }
        return IntPtr.Zero;
    }

    private void QueueTextCapture(uint sequence, DateTimeOffset capturedAt)
    {
        if (_disposed || !CanCaptureText || IsArmed || sequence == 0 || sequence == _lastTextSequence)
        {
            return;
        }

        if (_textRetryCancellation is not null && _textRetrySequence != sequence) CancelTextRetry();
        // Read this notification immediately, even if an older retry is waiting.
        // A retry flag must never block an observable new clipboard snapshot.
        var state = TryCaptureText(sequence, capturedAt);
        if (state == ClipboardReadState.Busy && _textRetryCancellation is null)
        {
            var cancellation = new CancellationTokenSource();
            _textRetryCancellation = cancellation;
            _textRetrySequence = sequence;
            _ = RetryTextAsync(sequence, capturedAt, cancellation);
        }
        else if (state == ClipboardReadState.Changed)
        {
            QueueLatestText();
        }
        else if (state != ClipboardReadState.Busy)
        {
            CancelTextRetry();
        }
    }

    private async Task TryConsumeImageAsync(uint sequence, DateTimeOffset capturedAt, long generation)
    {
        for (var attempt = 0; attempt < 10 && IsArmed && generation == _armGeneration; attempt++)
        {
            var snapshot = ClipboardSnapshotReader.Read(sequence, capturedAt,
                NativeMethods.GetClipboardSequenceNumber, ReadClipboardImage, value => value.Length > 0, IsTransientBusy);
            if (snapshot.State == ClipboardReadState.Captured && IsArmed && generation == _armGeneration)
            {
                LastImageCapturedAt = snapshot.CapturedAt;
                LastImageSequence = snapshot.Sequence;
                var handler = ImageCaptured;
                Disarm();
                handler?.Invoke(this, snapshot.Value!);
                return;
            }
            if (snapshot.State == ClipboardReadState.Changed)
            {
                sequence = NativeMethods.GetClipboardSequenceNumber();
                capturedAt = DateTimeOffset.Now;
                if (sequence == 0 || sequence == _armedSequence) break;
                continue;
            }
            if (snapshot.State != ClipboardReadState.Busy) break;
            await Task.Delay(100);
        }
        if (generation == _armGeneration) _processing = false;
    }

    private ClipboardReadState TryCaptureText(uint sequence, DateTimeOffset capturedAt)
    {
        var snapshot = ClipboardSnapshotReader.Read(sequence, capturedAt,
            NativeMethods.GetClipboardSequenceNumber, ReadClipboardText,
            value => !string.IsNullOrWhiteSpace(value), IsTransientBusy);
        if (snapshot.State is ClipboardReadState.Captured or ClipboardReadState.Ignored or ClipboardReadState.Failed)
            _lastTextSequence = snapshot.Sequence;
        if (snapshot.State != ClipboardReadState.Captured) return snapshot.State;
        if (ConsumeInternalText(snapshot.Value!, snapshot.Sequence)) return ClipboardReadState.Ignored;
        LastTextCapturedAt = snapshot.CapturedAt;
        LastTextSequence = snapshot.Sequence;
        TextCaptured?.Invoke(this, ClipboardTextCapturePolicy.Truncate(snapshot.Value!));
        return ClipboardReadState.Captured;
    }

    private async Task RetryTextAsync(uint sequence, DateTimeOffset capturedAt, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            for (var attempt = 0; attempt < 10 && CanCaptureText && !IsArmed && !_disposed; attempt++)
            {
                await Task.Delay(75, token);
                if (_disposed || !CanCaptureText || IsArmed || sequence == _lastTextSequence) return;
                var state = TryCaptureText(sequence, capturedAt);
                if (state == ClipboardReadState.Changed)
                {
                    QueueLatestText();
                    return;
                }
                if (state != ClipboardReadState.Busy) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_textRetryCancellation, cancellation))
            {
                _textRetryCancellation = null;
                _textRetrySequence = 0;
            }
            cancellation.Dispose();
        }
    }

    private void CancelTextRetry()
    {
        var cancellation = _textRetryCancellation;
        _textRetryCancellation = null;
        _textRetrySequence = 0;
        cancellation?.Cancel();
    }

    private void QueueLatestText()
    {
        var dispatcher = _source?.Dispatcher ?? Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => QueueTextCapture(NativeMethods.GetClipboardSequenceNumber(), DateTimeOffset.Now));
    }

    private static byte[]? ReadClipboardImage()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            var path = files.Count == 1 ? files[0] : null;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && IsImagePath(path))
            {
                return File.ReadAllBytes(path);
            }
        }

        if (!Clipboard.ContainsImage())
        {
            return null;
        }

        var image = Clipboard.GetImage();
        if (image is null)
        {
            return null;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private bool CanCaptureText => TextCaptureEnabled && !TextCapturePaused;

    private string? ReadClipboardText()
    {
        // Images and one-file drops are handled by the PixPin flow. Ignoring them
        // here avoids collecting auxiliary text formats from a copied screenshot.
        var text = ReadNativeClipboardText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (_textPrivacyFilter.ShouldExcludeCurrentClipboardOwner()
            || TextPrivacyModeEnabled && _textPrivacyFilter.LooksSensitive(text))
        {
            return null;
        }

        return text;
    }

    private static bool ConsumeInternalText(string text, uint sequence)
    {
        lock (InternalTextLock)
        {
            if (_internalText is null || DateTimeOffset.UtcNow > _internalTextExpiresAt)
            {
                _internalText = null;
                _internalTextSequence = 0;
                return false;
            }

            if (_internalTextSequence != 0 && _internalTextSequence != sequence
                || !string.Equals(_internalText, text, StringComparison.Ordinal))
            {
                return false;
            }

            _internalText = null;
            _internalTextSequence = 0;
            _internalTextExpiresAt = default;
            return true;
        }
    }

    private static bool IsImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";

    public void Dispose()
    {
        _disposed = true;
        CancelTextRetry();
        Disarm();
        if (_source is not null)
        {
            NativeMethods.RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WindowProc);
            _source = null;
        }
    }

    private static string NormalizeText(string text) => text.Replace("\r\n", "\n").Trim();

    private static bool IsTransientBusy(Exception error) =>
        error is ExternalException { ErrorCode: unchecked((int)0x800401D0) };

    // One immediate open attempt avoids WPF's internal OLE retry loop. The
    // clipboard is unlocked before privacy checks or any capture event runs.
    private static string? ReadNativeClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new ExternalException("Clipboard is busy.", unchecked((int)0x800401D0));
        try
        {
            if (IsClipboardFormatAvailable(2) || IsClipboardFormatAvailable(8)
                || IsClipboardFormatAvailable(17) || IsClipboardFormatAvailable(15)
                || !IsClipboardFormatAvailable(13)) return null;
            var handle = GetClipboardData(13);
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try { return NormalizeText(Marshal.PtrToStringUni(pointer) ?? ""); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
