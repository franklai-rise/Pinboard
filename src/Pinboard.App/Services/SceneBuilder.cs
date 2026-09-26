using System.Text.Json;
using System.Text.Json.Nodes;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public static class SceneBuilder
{
    public static string CreateDefaultScene() => CreateScene(LocalizationService.T("SceneInboxLayoutLabel"));

    public static string CreateTextClipsScene() => CreateScene(LocalizationService.T("SceneTextClipsLayoutLabel"));

    private static string CreateScene(string heading)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var root = new JsonObject
        {
            ["type"] = "excalidraw",
            ["version"] = 2,
            ["source"] = "pinboard://offline",
            ["elements"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "pinboard-inbox-title",
                    ["type"] = "text",
                    ["x"] = 80,
                    ["y"] = 20,
                    ["width"] = 390,
                    ["height"] = 30,
                    ["angle"] = 0,
                    ["strokeColor"] = "#657080",
                    ["backgroundColor"] = "transparent",
                    ["fillStyle"] = "solid",
                    ["strokeWidth"] = 2,
                    ["strokeStyle"] = "solid",
                    ["roughness"] = 0,
                    ["opacity"] = 100,
                    ["groupIds"] = new JsonArray(),
                    ["frameId"] = null,
                    ["roundness"] = null,
                    ["seed"] = 19760823,
                    ["version"] = 1,
                    ["versionNonce"] = 91827364,
                    ["isDeleted"] = false,
                    ["boundElements"] = null,
                    ["updated"] = now,
                    ["link"] = null,
                    ["locked"] = true,
                    ["text"] = heading,
                    ["fontSize"] = 24,
                    ["fontFamily"] = 5,
                    ["textAlign"] = "left",
                    ["verticalAlign"] = "top",
                    ["containerId"] = null,
                    ["originalText"] = heading,
                    ["autoResize"] = true,
                    ["lineHeight"] = 1.25
                }
            },
            ["appState"] = DefaultAppState(),
            ["files"] = new JsonObject()
        };
        return root.ToJsonString();
    }

    public static InsertResult AppendImage(
        string sceneJson,
        string fileId,
        AssetRecord asset,
        double x,
        double y,
        string source)
    {
        var root = JsonNode.Parse(sceneJson)?.AsObject()
            ?? throw new InvalidDataException(LocalizationService.T("SceneInvalid"));
        var elements = root["elements"]?.AsArray() ?? new JsonArray();
        root["elements"] = elements;

        var displayWidth = Math.Min(asset.Width, 1120d);
        var displayHeight = asset.Height * (displayWidth / asset.Width);
        var elementId = $"img-{Guid.NewGuid():N}"[..24];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        elements.Add(new JsonObject
        {
            ["id"] = elementId,
            ["type"] = "image",
            ["x"] = x,
            ["y"] = y,
            ["width"] = displayWidth,
            ["height"] = displayHeight,
            ["angle"] = 0,
            ["strokeColor"] = "transparent",
            ["backgroundColor"] = "transparent",
            ["fillStyle"] = "solid",
            ["strokeWidth"] = 2,
            ["strokeStyle"] = "solid",
            ["roughness"] = 0,
            ["opacity"] = 100,
            ["groupIds"] = new JsonArray(),
            ["frameId"] = null,
            ["roundness"] = null,
            ["seed"] = Random.Shared.Next(1, int.MaxValue),
            ["version"] = 1,
            ["versionNonce"] = Random.Shared.Next(1, int.MaxValue),
            ["isDeleted"] = false,
            ["boundElements"] = null,
            ["updated"] = now,
            ["link"] = null,
            ["locked"] = false,
            ["fileId"] = fileId,
            ["status"] = "saved",
            ["scale"] = new JsonArray(1, 1),
            ["crop"] = null,
            ["customData"] = new JsonObject
            {
                ["pinboard"] = new JsonObject
                {
                    ["role"] = "inbox-item",
                    ["capturedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["source"] = source,
                    ["ocrStatus"] = "pending"
                }
            }
        });

        root["appState"] ??= DefaultAppState();
        root["files"] = new JsonObject();
        return new InsertResult(elementId, fileId, asset.Hash, x, y, displayWidth, displayHeight, root.ToJsonString());
    }

    public static TextClipInsertResult AppendTextClip(
        string sceneJson,
        string text,
        double x,
        double y,
        string source)
    {
        var root = JsonNode.Parse(sceneJson)?.AsObject()
            ?? throw new InvalidDataException(LocalizationService.T("SceneInvalid"));
        var elements = root["elements"]?.AsArray() ?? new JsonArray();
        root["elements"] = elements;

        var capturedAt = DateTimeOffset.Now;
        var originalText = text.Replace("\r\n", "\n").Trim();
        var displayedText = WrapTextForCard(originalText, 70, out var lineCount);
        const double cardWidth = 820;
        const double contentXInset = 28;
        const double contentWidth = 764;
        const double contentLineHeight = 25;
        var contentHeight = Math.Max(contentLineHeight, lineCount * contentLineHeight);
        var cardHeight = Math.Max(116, 66 + contentHeight + 24);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var groupId = $"clip-group-{Guid.NewGuid():N}";
        var cardId = $"clip-card-{Guid.NewGuid():N}"[..24];
        var metaId = $"clip-meta-{Guid.NewGuid():N}"[..24];
        var contentId = $"clip-text-{Guid.NewGuid():N}"[..24];
        var metadataText = LocalizationService.T("SceneTextClipTimestampFormat", capturedAt.LocalDateTime);
        var groupIds = new JsonArray(groupId);

        elements.Add(new JsonObject
        {
            ["id"] = cardId,
            ["type"] = "rectangle",
            ["x"] = x,
            ["y"] = y,
            ["width"] = cardWidth,
            ["height"] = cardHeight,
            ["angle"] = 0,
            ["strokeColor"] = "#e1e5eb",
            ["backgroundColor"] = "#fafbfc",
            ["fillStyle"] = "solid",
            ["strokeWidth"] = 1,
            ["strokeStyle"] = "solid",
            ["roughness"] = 0,
            ["opacity"] = 100,
            ["groupIds"] = groupIds.DeepClone(),
            ["frameId"] = null,
            ["roundness"] = new JsonObject { ["type"] = 3 },
            ["seed"] = Random.Shared.Next(1, int.MaxValue),
            ["version"] = 1,
            ["versionNonce"] = Random.Shared.Next(1, int.MaxValue),
            ["isDeleted"] = false,
            ["boundElements"] = null,
            ["updated"] = now,
            ["link"] = null,
            ["locked"] = false,
            ["customData"] = new JsonObject
            {
                ["pinboard"] = new JsonObject
                {
                    ["role"] = "text-clip-card",
                    ["capturedAt"] = capturedAt.ToString("O"),
                    ["source"] = source
                }
            }
        });

        elements.Add(CreateTextElement(
            metaId,
            metadataText,
            metadataText,
            x + contentXInset,
            y + 18,
            contentWidth,
            18,
            11,
            "#79828f",
            groupIds,
            now,
            locked: false));

        elements.Add(CreateTextElement(
            contentId,
            displayedText,
            originalText,
            x + contentXInset,
            y + 46,
            contentWidth,
            contentHeight,
            18,
            "#1d1d1f",
            groupIds,
            now,
            locked: false,
            customData: new JsonObject
            {
                ["pinboard"] = new JsonObject
                {
                    ["role"] = "text-clip",
                    ["capturedAt"] = capturedAt.ToString("O"),
                    ["source"] = source
                }
            }));

        root["appState"] ??= DefaultAppState();
        root["files"] ??= new JsonObject();
        return new TextClipInsertResult(contentId, x, y, cardWidth, cardHeight, root.ToJsonString());
    }

    public static IReadOnlyList<(string Id, string Text, double X, double Y)> ExtractTextElements(string sceneJson)
    {
        using var document = JsonDocument.Parse(sceneJson);
        if (!document.RootElement.TryGetProperty("elements", out var elements))
        {
            return [];
        }

        var result = new List<(string, string, double, double)>();
        foreach (var element in elements.EnumerateArray())
        {
            if (element.TryGetProperty("isDeleted", out var deleted) && deleted.GetBoolean())
            {
                continue;
            }

            if (!element.TryGetProperty("type", out var type) || type.GetString() != "text")
            {
                continue;
            }

            var text = element.TryGetProperty("originalText", out var original)
                ? original.GetString()
                : element.TryGetProperty("text", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            result.Add((
                element.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                text,
                element.TryGetProperty("x", out var x) ? x.GetDouble() : 0,
                element.TryGetProperty("y", out var y) ? y.GetDouble() : 0));
        }
        return result;
    }

    private static JsonObject DefaultAppState() => new()
    {
        ["viewBackgroundColor"] = "#ffffff",
        ["currentItemStrokeColor"] = "#1b1b1f",
        ["currentItemBackgroundColor"] = "transparent",
        ["currentItemFillStyle"] = "solid",
        ["currentItemStrokeWidth"] = 2,
        ["currentItemStrokeStyle"] = "solid",
        ["currentItemRoughness"] = 0,
        ["currentItemOpacity"] = 100,
        ["currentItemFontFamily"] = 5,
        ["currentItemFontSize"] = 20,
        ["currentItemTextAlign"] = "left",
        ["currentItemStartArrowhead"] = null,
        ["currentItemEndArrowhead"] = "arrow",
        ["scrollX"] = 0,
        ["scrollY"] = 0,
        ["zoom"] = new JsonObject { ["value"] = 0.8 },
        ["gridSize"] = null,
        ["colorPalette"] = new JsonObject()
    };

    private static JsonObject CreateTextElement(
        string id,
        string text,
        string originalText,
        double x,
        double y,
        double width,
        double height,
        int fontSize,
        string strokeColor,
        JsonArray groupIds,
        long now,
        bool locked,
        JsonObject? customData = null) => new()
    {
        ["id"] = id,
        ["type"] = "text",
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height,
        ["angle"] = 0,
        ["strokeColor"] = strokeColor,
        ["backgroundColor"] = "transparent",
        ["fillStyle"] = "solid",
        ["strokeWidth"] = 1,
        ["strokeStyle"] = "solid",
        ["roughness"] = 0,
        ["opacity"] = 100,
        ["groupIds"] = groupIds.DeepClone(),
        ["frameId"] = null,
        ["roundness"] = null,
        ["seed"] = Random.Shared.Next(1, int.MaxValue),
        ["version"] = 1,
        ["versionNonce"] = Random.Shared.Next(1, int.MaxValue),
        ["isDeleted"] = false,
        ["boundElements"] = null,
        ["updated"] = now,
        ["link"] = null,
        ["locked"] = locked,
        ["text"] = text,
        ["fontSize"] = fontSize,
        ["fontFamily"] = 5,
        ["textAlign"] = "left",
        ["verticalAlign"] = "top",
        ["containerId"] = null,
        ["originalText"] = originalText,
        ["autoResize"] = true,
        ["lineHeight"] = 1.25,
        ["customData"] = customData
    };

    private static string WrapTextForCard(string text, int maxUnitsPerLine, out int lineCount)
    {
        var result = new StringBuilder(text.Length + Math.Max(4, text.Length / maxUnitsPerLine));
        var units = 0;
        lineCount = 1;
        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }
            if (character == '\n')
            {
                result.Append('\n');
                units = 0;
                lineCount++;
                continue;
            }

            var characterUnits = character is >= '\u2E80' ? 2 : 1;
            if (units > 0 && units + characterUnits > maxUnitsPerLine)
            {
                result.Append('\n');
                units = 0;
                lineCount++;
            }
            result.Append(character);
            units += characterUnits;
        }
        return result.ToString();
    }
}
