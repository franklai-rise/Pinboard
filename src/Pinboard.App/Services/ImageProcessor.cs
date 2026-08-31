using System.Security.Cryptography;
using SkiaSharp;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed class ImageProcessor
{
    public AssetRecord Process(byte[] sourceBytes, int webpQuality)
    {
        using var bitmap = SKBitmap.Decode(sourceBytes)
            ?? throw new InvalidDataException(LocalizationService.T("ImageDecodeFailed"));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, Math.Clamp(webpQuality, 1, 100));

        byte[] bytes;
        string mime;
        if (encoded is not null && encoded.Size > 0)
        {
            bytes = encoded.ToArray();
            mime = "image/webp";
        }
        else
        {
            bytes = sourceBytes;
            mime = DetectMimeType(sourceBytes);
        }

        return new AssetRecord(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            mime,
            bitmap.Width,
            bitmap.Height,
            bytes,
            DateTimeOffset.UtcNow);
    }

    public static (byte[] Bytes, string MimeType) DecodeDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(LocalizationService.T("ImageDataUrlInvalid"));
        }

        var header = dataUrl[5..comma];
        var mime = header.Split(';', 2)[0];
        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        return (bytes, mime);
    }

    public static string ToDataUrl(string mimeType, byte[] bytes) =>
        $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

    public static string DetectMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "image/png";
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return "image/bmp";
        }

        return "application/octet-stream";
    }
}
