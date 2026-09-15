using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Pinboard.App.Interop;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed class ClipboardCaptureService : IDisposable
{
    private const int MaxCapturedTextLength = 100_000;
    private const string TruncatedTextMarker = "\n\n[Pinboard: clipboard text was truncated at 100,000 characters.]";
    private static readonly object InternalTextLock = new();
    private static string? _internalText;
    private static DateTimeOffset _internalTextExpiresAt;
    private HwndSource? _source;
    private uint _armedSequence;
    private uint _lastTextSequence;
    private CancellationTokenSource? _armCancellation;
    private bool _processing;
    private bool _textProcessing;
    private ClipboardTextPrivacyFilter _textPrivacyFilter = new(AppSettings.CreateDefaultExcludedApplications());

    public event EventHandler<byte[]>? ImageCaptured;
    public event EventHandler<string>? TextCaptured;
    public event EventHandler? CaptureTimedOut;

    public bool IsArmed => _armCancellation is { IsCancellationRequested: false };
    public bool TextCaptureEnabled { get; set; }
    public bool TextCapturePaused { get; set; }
    public bool TextPrivacyModeEnabled { get; set; } = true;

    public void ConfigureTextCapture(AppSettings settings)
    {
        TextCaptureEnabled = settings.TextCaptureEnabled;
        TextCapturePaused = settings.TextCapturePaused;
        TextPrivacyModeEnabled = settings.TextPrivacyModeEnabled;
        _textPrivacyFilter = new ClipboardTextPrivacyFilter(settings.TextCaptureExcludedApplications);
    }

    /// <summary>
    /// Copies application-owned text without adding it to the automatic Text Clips board.
    /// This prevents commands such as “Copy file path” from collecting themselves.
    /// </summary>
    public static void SetTextWithoutCapture(string text)
    {
        lock (InternalTextLock)
        {
            _internalText = text;
            _internalTextExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            lock (InternalTextLock)
            {
                _internalText = null;
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
        _armedSequence = NativeMethods.GetClipboardSequenceNumber();
        _armCancellation = new CancellationTokenSource(timeout);
        _armCancellation.Token.Register(() =>
        {
            if (!_processing)
            {
                Application.Current.Dispatcher.BeginInvoke(() => CaptureTimedOut?.Invoke(this, EventArgs.Empty));
            }
        });
    }

    public void Disarm()
    {
        _armCancellation?.Cancel();
        _armCancellation?.Dispose();
        _armCancellation = null;
        _processing = false;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmClipboardUpdate)
        {
            return IntPtr.Zero;
        }

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (IsArmed && !_processing && sequence != 0 && sequence != _armedSequence)
        {
            _processing = true;
            _ = TryConsumeImageAsync();
        }

        // An armed PixPin capture owns the next clipboard change. Do not treat any
        // temporary text format it publishes as a normal text clip.
        if (!IsArmed)
        {
            QueueTextCapture(sequence);
        }
        return IntPtr.Zero;
    }

    private void QueueTextCapture(uint sequence)
    {
        if (!CanCaptureText || _textProcessing || sequence == 0 || sequence == _lastTextSequence)
        {
            return;
        }

        _lastTextSequence = sequence;
        _textProcessing = true;
        _ = TryConsumeTextAsync();
    }

    private async Task TryConsumeImageAsync()
    {
        for (var attempt = 0; attempt < 10 && IsArmed; attempt++)
        {
            try
            {
                var bytes = ReadClipboardImage();
                if (bytes is { Length: > 0 })
                {
                    var handler = ImageCaptured;
                    Disarm();
                    handler?.Invoke(this, bytes);
                    return;
                }
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // PixPin can hold the clipboard briefly while publishing all formats.
            }

            await Task.Delay(100);
        }
        _processing = false;
    }

    private async Task TryConsumeTextAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 10 && CanCaptureText && !IsArmed; attempt++)
            {
                try
                {
                    var text = ReadClipboardText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // The clipboard can change again while a previous read is
                        // settling. Remember the actual sequence that produced this
                        // text so the final catch-up check does not save it twice.
                        _lastTextSequence = NativeMethods.GetClipboardSequenceNumber();
                        TextCaptured?.Invoke(this, text);
                        return;
                    }
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Another program can briefly own the clipboard while publishing text.
                }

                await Task.Delay(75);
            }
        }
        finally
        {
            _textProcessing = false;

            // If a rapid second copy happened while the first clipboard read was
            // settling, process the newest clipboard item next.
            if (!IsArmed)
            {
                QueueTextCapture(NativeMethods.GetClipboardSequenceNumber());
            }
        }
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
        if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList() || !Clipboard.ContainsText())
        {
            return null;
        }

        var text = Clipboard.GetText().Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(text) || ConsumeInternalText(text))
        {
            return null;
        }

        if (_textPrivacyFilter.ShouldExcludeCurrentClipboardOwner()
            || TextPrivacyModeEnabled && _textPrivacyFilter.LooksSensitive(text))
        {
            return null;
        }

        if (text.Length <= MaxCapturedTextLength)
        {
            return text;
        }

        return text[..(MaxCapturedTextLength - TruncatedTextMarker.Length)] + TruncatedTextMarker;
    }

    private static bool ConsumeInternalText(string text)
    {
        lock (InternalTextLock)
        {
            if (_internalText is null || DateTimeOffset.UtcNow > _internalTextExpiresAt)
            {
                _internalText = null;
                return false;
            }

            if (!string.Equals(_internalText, text, StringComparison.Ordinal))
            {
                return false;
            }

            _internalText = null;
            _internalTextExpiresAt = default;
            return true;
        }
    }

    private static bool IsImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";

    public void Dispose()
    {
        Disarm();
        if (_source is not null)
        {
            NativeMethods.RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WindowProc);
            _source = null;
        }
    }
}
