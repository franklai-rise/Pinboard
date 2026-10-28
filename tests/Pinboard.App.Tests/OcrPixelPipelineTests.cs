using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services;
using SkiaSharp;
using Windows.Media.Ocr;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class OcrPixelPipelineTests
{
    [TestMethod]
    public async Task DirectPixels_RecognizeEnglishAtBothEndsOfATallImageAndReuseEngines()
    {
        var height = Math.Max(512, (int)OcrEngine.MaxImageDimension) + 300;
        using var bitmap = new SKBitmap(720, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        using (var typeface = SKTypeface.FromFamilyName("Arial"))
        using (var font = new SKFont(typeface, 48))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("PINBOARD MEMORY", 32, 90, SKTextAlign.Left, font, paint);
            canvas.DrawText("CAPTURE RESTORED", 32, height - 60, SKTextAlign.Left, font, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var service = new OcrService();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var results = await service.RecognizeAsync(encoded.ToArray(), chinese: true, english: true);
            Assert.AreEqual(2, results.Count);
            foreach (var result in results) Assert.AreNotEqual("failed", result.Status, result.Error);
            var english = results.Single(result => result.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (english.Status == "complete")
            {
                StringAssert.Contains(english.Text.ToUpperInvariant(), "MEMORY");
                StringAssert.Contains(english.Text.ToUpperInvariant(), "CAPTURE");
            }
        }
    }
}
