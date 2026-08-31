using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pinboard.App.Compatibility;
using Pinboard.App.Models;
using SkiaSharp;

namespace Pinboard.App.Services;

public sealed partial class ObsidianImporter
{
    private readonly ImageProcessor _imageProcessor;
    private readonly OcrService _ocrService;

    public ObsidianImporter(ImageProcessor imageProcessor, OcrService ocrService)
    {
        _imageProcessor = imageProcessor;
        _ocrService = ocrService;
    }

    public async Task<ImportReport> ImportAsync(
        string sourcePath,
        string destinationPath,
        int webpQuality,
        bool ocrChinese,
        bool ocrEnglish,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var vaultRoot = FindVaultRoot(sourcePath)
            ?? throw new InvalidDataException(LocalizationService.T("ImportVaultRootMissing"));
        var markdown = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        var scene = DecodeScene(markdown);
        var mappings = ParseEmbeddedFiles(markdown);
        var attachmentFolder = ReadAttachmentFolder(vaultRoot);
        var warnings = new List<string>();
        var imported = new List<ImportedAsset>();
        var placeholderCount = 0;

        var root = JsonNode.Parse(scene)?.AsObject()
            ?? throw new InvalidDataException(LocalizationService.T("ImportSceneInvalid"));
        var elements = root["elements"]?.AsArray() ?? new JsonArray();
        root["files"] = new JsonObject();

        foreach (var elementNode in elements)
        {
            if (elementNode is not JsonObject element || element["type"]?.GetValue<string>() != "image")
            {
                continue;
            }
            if (element["isDeleted"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var fileId = element["fileId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fileId) || !mappings.TryGetValue(fileId, out var mapping))
            {
                warnings.Add($"图片元素 {element["id"]} 没有可解析的 Embedded Files 映射。");
                continue;
            }

            byte[] originalBytes;
            var isPlaceholder = string.Equals(mapping, "markdown-image", StringComparison.OrdinalIgnoreCase);
            if (isPlaceholder)
            {
                originalBytes = CreateMarkdownPlaceholder(
                    Math.Max(1, (int)Math.Round(element["width"]?.GetValue<double>() ?? 500)),
                    Math.Max(1, (int)Math.Round(element["height"]?.GetValue<double>() ?? 85)));
                placeholderCount++;
                warnings.Add($"{Path.GetFileName(sourcePath)} 中的空 Markdown-image 已转换为静态占位图。");
            }
            else
            {
                var mediaPath = ResolveMediaPath(vaultRoot, sourcePath, attachmentFolder, mapping);
                if (mediaPath is null)
                {
                    originalBytes = CreateMissingPlaceholder(500, 120);
                    placeholderCount++;
                    warnings.Add($"未找到图片：{mapping}，已生成占位图。");
                    isPlaceholder = true;
                }
                else
                {
                    originalBytes = await File.ReadAllBytesAsync(mediaPath, cancellationToken);
                }
            }

            var asset = _imageProcessor.Process(originalBytes, webpQuality);
            var x = element["x"]?.GetValue<double>() ?? 0;
            var y = element["y"]?.GetValue<double>() ?? 0;
            imported.Add(new ImportedAsset(fileId, asset, originalBytes, x, y, isPlaceholder));
            element["status"] = "saved";
        }

        return await PersistImportedSceneAsync(
            sourcePath,
            destinationPath,
            root,
            elements,
            imported,
            placeholderCount,
            warnings,
            ocrChinese,
            ocrEnglish,
            "obsidian-import",
            cancellationToken);
    }

    public async Task<ImportReport> ImportStandardAsync(
        string sourcePath,
        string destinationPath,
        int webpQuality,
        bool ocrChinese,
        bool ocrEnglish,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var json = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException(LocalizationService.T("ImportStandardInvalid"));
        var elements = root["elements"]?.AsArray() ?? new JsonArray();
        var sourceFiles = root["files"]?.AsObject() ?? new JsonObject();
        var warnings = new List<string>();
        var imported = new List<ImportedAsset>();
        var placeholderCount = 0;

        foreach (var elementNode in elements)
        {
            if (elementNode is not JsonObject element || element["type"]?.GetValue<string>() != "image" || element["isDeleted"]?.GetValue<bool>() == true)
            {
                continue;
            }
            var fileId = element["fileId"]?.GetValue<string>();
            byte[] originalBytes;
            var isPlaceholder = false;
            if (string.IsNullOrWhiteSpace(fileId)
                || sourceFiles[fileId] is not JsonObject file
                || file["dataURL"]?.GetValue<string>() is not { } dataUrl)
            {
                originalBytes = CreateMissingPlaceholder(500, 120);
                fileId ??= $"missing-{Guid.NewGuid():N}";
                element["fileId"] = fileId;
                isPlaceholder = true;
                placeholderCount++;
                warnings.Add($"图片元素 {element["id"]} 缺少内嵌图片，已生成占位图。");
            }
            else
            {
                originalBytes = ImageProcessor.DecodeDataUrl(dataUrl).Bytes;
            }

            var asset = _imageProcessor.Process(originalBytes, webpQuality);
            var x = element["x"]?.GetValue<double>() ?? 0;
            var y = element["y"]?.GetValue<double>() ?? 0;
            imported.Add(new ImportedAsset(fileId, asset, originalBytes, x, y, isPlaceholder));
            element["status"] = "saved";
        }
        root["files"] = new JsonObject();

        return await PersistImportedSceneAsync(
            sourcePath,
            destinationPath,
            root,
            elements,
            imported,
            placeholderCount,
            warnings,
            ocrChinese,
            ocrEnglish,
            "excalidraw-import",
            cancellationToken);
    }

    private async Task<ImportReport> PersistImportedSceneAsync(
        string sourcePath,
        string destinationPath,
        JsonObject root,
        JsonArray elements,
        IReadOnlyList<ImportedAsset> imported,
        int placeholderCount,
        IReadOnlyList<string> warnings,
        bool ocrChinese,
        bool ocrEnglish,
        string checkpointReason,
        CancellationToken cancellationToken)
    {
        var destination = GetAvailableDestination(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".importing-{Guid.NewGuid():N}";
        try
        {
            var tempDocument = new PinboardDocument(temporary);
            await tempDocument.InitializeAsync(cancellationToken);
            await tempDocument.SaveSceneAsync(
                root.ToJsonString(),
                imported.Select(item => (item.FileId, item.Asset)).ToList(),
                checkpoint: true,
                checkpointReason,
                cancellationToken);

            var maxBottom = elements
                .Where(node => node is JsonObject obj && obj["type"]?.GetValue<string>() == "image" && obj["isDeleted"]?.GetValue<bool>() != true)
                .Select(node =>
                {
                    var obj = (JsonObject)node!;
                    return (obj["y"]?.GetValue<double>() ?? 0) + (obj["height"]?.GetValue<double>() ?? 0);
                })
                .DefaultIfEmpty(16)
                .Max();
            await tempDocument.ResetInboxAsync(80, maxBottom + 64, cancellationToken);
            File.Move(temporary, destination);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            throw;
        }

        var document = new PinboardDocument(destination);
        foreach (var item in imported)
        {
            if (item.IsPlaceholder)
            {
                await document.SaveOcrAsync(item.Asset.Hash, "none", string.Empty, "skipped", "占位图不执行 OCR。", item.X, item.Y, cancellationToken);
                continue;
            }
            var results = await _ocrService.RecognizeAsync(item.OriginalBytes, ocrChinese, ocrEnglish, cancellationToken);
            foreach (var result in results)
            {
                await document.SaveOcrAsync(item.Asset.Hash, result.Language, result.Text, result.Status, result.Error, item.X, item.Y, cancellationToken);
            }
        }

        return new ImportReport(
            sourcePath,
            destination,
            elements.Count(node => node is JsonObject obj && obj["isDeleted"]?.GetValue<bool>() != true),
            imported.Count,
            placeholderCount,
            warnings);
    }

    public static string DecodeScene(string markdown)
    {
        var match = DrawingRegex().Match(markdown);
        if (!match.Success)
        {
            throw new InvalidDataException(LocalizationService.T("ImportCompressedMissing"));
        }
        var encoded = WhitespaceRegex().Replace(match.Groups[1].Value, string.Empty);
        var decoded = LzStringCodec.DecompressFromBase64(encoded)
            ?? throw new InvalidDataException(LocalizationService.T("ImportDecompressFailed"));
        var lastBrace = decoded.LastIndexOf('}');
        if (lastBrace < 0)
        {
            throw new InvalidDataException(LocalizationService.T("ImportDecompressedInvalid"));
        }
        return decoded[..(lastBrace + 1)];
    }

    public static IReadOnlyDictionary<string, string> ParseEmbeddedFiles(string markdown)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = EmbeddedSectionRegex().Match(markdown);
        if (!section.Success)
        {
            return result;
        }

        foreach (Match match in EmbeddedLineRegex().Matches(section.Groups[1].Value))
        {
            var target = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            result[match.Groups[1].Value] = target.Trim();
        }
        return result;
    }

    private static string? FindVaultRoot(string sourcePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".obsidian")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static string ReadAttachmentFolder(string vaultRoot)
    {
        try
        {
            var appJson = Path.Combine(vaultRoot, ".obsidian", "app.json");
            using var document = JsonDocument.Parse(File.ReadAllText(appJson));
            return document.RootElement.TryGetProperty("attachmentFolderPath", out var value)
                ? value.GetString() ?? "_media"
                : "_media";
        }
        catch
        {
            return "_media";
        }
    }

    private static string? ResolveMediaPath(string vaultRoot, string boardPath, string attachmentFolder, string rawLink)
    {
        var link = rawLink.Split('|', 2)[0].Split('#', 2)[0].Trim().Replace('/', Path.DirectorySeparatorChar);
        var candidates = new[]
        {
            Path.Combine(vaultRoot, link),
            Path.Combine(Path.GetDirectoryName(boardPath)!, link),
            Path.Combine(vaultRoot, attachmentFolder, link)
        };
        var canonicalRoot = Path.GetFullPath(vaultRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                return full;
            }
        }

        var matches = Directory.EnumerateFiles(vaultRoot, Path.GetFileName(link), SearchOption.AllDirectories).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string GetAvailableDestination(string destination)
    {
        destination = Path.GetFullPath(destination);
        if (!File.Exists(destination))
        {
            return destination;
        }
        var directory = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileNameWithoutExtension(destination);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-imported-{index}.pinboard");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static byte[] CreateMarkdownPlaceholder(int width, int height) => CreatePlaceholder(width, height, new SKColor(246, 244, 252), new SKColor(113, 87, 217));
    private static byte[] CreateMissingPlaceholder(int width, int height) => CreatePlaceholder(width, height, new SKColor(255, 244, 229), new SKColor(230, 119, 0));

    private static byte[] CreatePlaceholder(int width, int height, SKColor background, SKColor border)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        using var paint = new SKPaint { Color = border, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        canvas.DrawRect(1.5f, 1.5f, width - 3, height - 3, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed record ImportedAsset(string FileId, AssetRecord Asset, byte[] OriginalBytes, double X, double Y, bool IsPlaceholder);

    [GeneratedRegex(@"(?:^|\n)##? Drawing\r?\n[^`]*```compressed-json\r?\n([\s\S]*?)```\r?(?:\n|$)", RegexOptions.Multiline)]
    private static partial Regex DrawingRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?:^|\n)## Embedded Files\r?\n([\s\S]*?)(?=\r?\n## |\r?\n# |\z)", RegexOptions.Multiline)]
    private static partial Regex EmbeddedSectionRegex();

    [GeneratedRegex(@"^([\w\d]+):\s*(?:!?\[\[([^\]]*)\]\]|([^\r\n]+))\s*(?:\{[^}]*\})?\s*$", RegexOptions.Multiline)]
    private static partial Regex EmbeddedLineRegex();
}
