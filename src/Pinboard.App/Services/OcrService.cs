using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace Pinboard.App.Services;

public sealed record OcrTextResult(string Language, string Text, string Status, string? Error);

public interface IOcrRecognizer
{
    Task<IReadOnlyList<OcrTextResult>> RecognizeAsync(
        byte[] sourceBytes, bool chinese, bool english, CancellationToken cancellationToken = default);
}

public sealed class OcrService : IOcrRecognizer
{
    private readonly SemaphoreSlim _ocrGate = new(1, 1);
    // Engines are reused only while holding the same serial gate.
    private readonly Dictionary<string, OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<OcrTextResult>> RecognizeAsync(
        byte[] sourceBytes,
        bool chinese,
        bool english,
        CancellationToken cancellationToken = default)
    {
        await _ocrGate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<OcrTextResult>();
            var sessions = new List<LanguageSession>(2);
            if (chinese)
            {
                AddLanguage(sessions, results, "zh-Hans", "zh-Hans-CN", "zh-CN", "zh-Hans");
            }
            if (english)
            {
                AddLanguage(sessions, results, "en", "en-US", "en-GB", "en");
            }
            if (sessions.Count == 0)
            {
                return results;
            }

            try
            {
                // One source decode serves both languages. Each bounded tile is
                // copied as BGRA pixels, avoiding intermediate PNG encode/decode.
                using var source = SKBitmap.Decode(sourceBytes)
                    ?? throw new InvalidDataException("OCR could not decode the image.");
                var max = Math.Max(512, (int)OcrEngine.MaxImageDimension);
                var tileStride = Math.Max(448, max - 64);
                for (var y = 0; y < source.Height; y += tileStride)
                {
                    for (var x = 0; x < source.Width; x += tileStride)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var width = Math.Min(max, source.Width - x);
                        var height = Math.Min(max, source.Height - y);
                        using var tile = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using (var canvas = new SKCanvas(tile))
                        {
                            canvas.Clear(SKColors.Transparent);
                            canvas.DrawBitmap(source,
                                new SKRect(x, y, x + width, y + height),
                                new SKRect(0, 0, width, height), SKSamplingOptions.Default);
                        }
                        using var softwareBitmap = new SoftwareBitmap(
                            BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
                        softwareBitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(tile.Bytes));
                        foreach (var session in sessions)
                        {
                            try
                            {
                                var recognized = await session.Engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
                                if (!string.IsNullOrWhiteSpace(recognized.Text))
                                {
                                    session.Chunks.Add(recognized.Text.Trim());
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                session.Error = exception.Message;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                foreach (var session in sessions)
                {
                    session.Error = exception.Message;
                }
            }
            results.AddRange(sessions.Select(session => new OcrTextResult(
                session.Language, string.Join(Environment.NewLine, session.Chunks),
                session.Error is null ? "complete" : "failed", session.Error)));
            return results;
        }
        finally
        {
            _ocrGate.Release();
        }
    }

    private static string? ResolveLanguageTag(params string[] preferredTags)
    {
        var available = OcrEngine.AvailableRecognizerLanguages
            .Select(language => language.LanguageTag)
            .ToList();
        foreach (var preferred in preferredTags)
        {
            var exact = available.FirstOrDefault(tag => tag.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }
        foreach (var preferred in preferredTags)
        {
            var prefix = preferred.Split('-', 2)[0];
            var match = available.FirstOrDefault(tag => tag.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }

    private void AddLanguage(List<LanguageSession> sessions, List<OcrTextResult> results,
        string fallback, params string[] preferredTags)
    {
        try
        {
            var tag = ResolveLanguageTag(preferredTags);
            if (tag is null)
            {
                results.Add(new OcrTextResult(fallback, string.Empty, "unavailable", $"Windows OCR language pack {fallback} is not installed."));
                return;
            }
            if (!_engines.TryGetValue(tag, out var engine))
            {
                engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
                if (engine is null)
                {
                    results.Add(new OcrTextResult(tag, string.Empty, "unavailable", $"Windows could not create the {tag} OCR engine."));
                    return;
                }
                _engines[tag] = engine;
            }
            sessions.Add(new LanguageSession(tag, engine));
        }
        catch (Exception exception)
        {
            results.Add(new OcrTextResult(fallback, string.Empty, "failed", exception.Message));
        }
    }

    private sealed class LanguageSession(string language, OcrEngine engine)
    {
        public string Language { get; } = language;
        public OcrEngine Engine { get; } = engine;
        public List<string> Chunks { get; } = [];
        public string? Error { get; set; }
    }
}
