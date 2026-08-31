using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Pinboard.App.Services;

public sealed record OcrTextResult(string Language, string Text, string Status, string? Error);

public sealed class OcrService
{
    private readonly SemaphoreSlim _ocrGate = new(1, 1);

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
            if (chinese)
            {
                var tag = ResolveLanguageTag("zh-Hans-CN", "zh-CN", "zh-Hans");
                results.Add(tag is null
                    ? new OcrTextResult("zh-Hans", string.Empty, "unavailable", "Windows 未安装简体中文 OCR 语言包。")
                    : await RecognizeLanguageAsync(sourceBytes, tag, cancellationToken));
            }
            if (english)
            {
                var tag = ResolveLanguageTag("en-US", "en-GB", "en");
                results.Add(tag is null
                    ? new OcrTextResult("en", string.Empty, "unavailable", "Windows 未安装英文 OCR 语言包。")
                    : await RecognizeLanguageAsync(sourceBytes, tag, cancellationToken));
            }
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

    private static async Task<OcrTextResult> RecognizeLanguageAsync(byte[] sourceBytes, string languageTag, CancellationToken cancellationToken)
    {
        try
        {
            var language = new Language(languageTag);
            if (!OcrEngine.IsLanguageSupported(language))
            {
                return new OcrTextResult(languageTag, string.Empty, "unavailable", $"Windows 未安装 {languageTag} OCR 语言包。");
            }

            var engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine is null)
            {
                return new OcrTextResult(languageTag, string.Empty, "unavailable", $"无法创建 {languageTag} OCR 引擎。");
            }

            using var bitmap = SKBitmap.Decode(sourceBytes)
                ?? throw new InvalidDataException("OCR 无法解码图片。");
            var max = Math.Max(512, (int)OcrEngine.MaxImageDimension);
            var tileSize = Math.Max(448, max - 64);
            var chunks = new List<string>();

            for (var y = 0; y < bitmap.Height; y += tileSize)
            {
                for (var x = 0; x < bitmap.Width; x += tileSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var width = Math.Min(max, bitmap.Width - x);
                    var height = Math.Min(max, bitmap.Height - y);
                    using var tile = new SKBitmap(width, height);
                    if (!bitmap.ExtractSubset(tile, new SKRectI(x, y, x + width, y + height)))
                    {
                        continue;
                    }
                    using var image = SKImage.FromBitmap(tile);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    var text = await RecognizePngAsync(engine, data.ToArray(), cancellationToken);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        chunks.Add(text.Trim());
                    }
                }
            }

            return new OcrTextResult(languageTag, string.Join(Environment.NewLine, chunks), "complete", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OcrTextResult(languageTag, string.Empty, "failed", ex.Message);
        }
    }

    private static async Task<string> RecognizePngAsync(OcrEngine engine, byte[] pngBytes, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        return result.Text ?? string.Empty;
    }
}
