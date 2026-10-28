using System.Globalization;
using System.Text.Json;

namespace Pinboard.App.Services;

/// <summary>A reading projection only. It never changes the source scene or discards repeated clips.</summary>
public sealed record TextReadingClip(
    string ElementId,
    string Text,
    DateTimeOffset? CapturedAt,
    string Source,
    int SceneIndex);

public sealed record TextReadingGroup(TextReadingClip Clip, IReadOnlyList<string> ElementIds)
{
    public int DuplicateCount => ElementIds.Count;
}

public static class TextReadingScene
{
    public static IReadOnlyList<TextReadingClip> Parse(string sceneJson)
    {
        using var document = JsonDocument.Parse(sceneJson);
        if (!document.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            return [];

        // Grouped timestamp labels are presentation, not another clipboard record.
        var clipGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements.EnumerateArray())
        {
            if (IsDeleted(element)) continue;
            var metadata = Metadata(element);
            if (String(metadata, "role") is "text-clip" or "text-clip-card")
                AddGroups(element, clipGroups);
        }

        var result = new List<TextReadingClip>();
        var index = 0;
        foreach (var element in elements.EnumerateArray())
        {
            var sceneIndex = index++;
            if (IsDeleted(element) || String(element, "type") != "text") continue;
            var id = String(element, "id");
            if (string.IsNullOrEmpty(id) || id == "pinboard-inbox-title") continue;
            var metadata = Metadata(element);
            var role = String(metadata, "role");
            if (role != "text-clip" && (id.StartsWith("clip-meta-", StringComparison.Ordinal) || SharesGroup(element, clipGroups)))
                continue;
            if (role is "text-clip-card" or "text-clip-meta") continue;

            var text = String(element, "originalText") ?? String(element, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var capturedAt = ParseDate(String(metadata, "capturedAt"));
            if (capturedAt is null && element.TryGetProperty("updated", out var updated) && updated.TryGetInt64(out var milliseconds))
            {
                try { capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
                catch (ArgumentOutOfRangeException) { /* Imported scenes can have invalid timestamps. */ }
            }
            result.Add(new TextReadingClip(id, text, capturedAt, String(metadata, "source") ?? "", sceneIndex));
        }
        return result;
    }

    public static IReadOnlyList<TextReadingGroup> Project(
        IReadOnlyList<TextReadingClip> clips,
        string? query,
        bool newestFirst,
        bool collapseConsecutiveDuplicates)
    {
        // Group before filtering: a different intervening record must keep two duplicate runs apart.
        var ordered = clips.OrderBy(clip => clip.CapturedAt?.UtcTicks ?? long.MinValue)
            .ThenBy(clip => clip.SceneIndex);
        var groups = new List<(TextReadingClip Clip, List<string> ElementIds)>();
        foreach (var clip in ordered)
        {
            if (collapseConsecutiveDuplicates && groups.Count > 0 && groups[^1].Clip.Text == clip.Text)
            {
                var previous = groups[^1];
                previous.ElementIds.Add(clip.ElementId);
                groups[^1] = (clip, previous.ElementIds);
            }
            else
                groups.Add((clip, [clip.ElementId]));
        }
        var trimmedQuery = query?.Trim();
        IEnumerable<(TextReadingClip Clip, List<string> ElementIds)> filtered = groups;
        if (!string.IsNullOrEmpty(trimmedQuery))
            filtered = filtered.Where(group => group.Clip.Text.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase));
        if (newestFirst) filtered = filtered.Reverse();
        return filtered.Select(group => new TextReadingGroup(group.Clip, group.ElementIds.ToArray())).ToArray();
    }

    public static string Excerpt(string text, string? query, int maximumLength = 260)
    {
        if (maximumLength < 16) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        var trimmedQuery = query?.Trim();
        var match = string.IsNullOrEmpty(trimmedQuery) ? -1 : text.IndexOf(trimmedQuery, StringComparison.OrdinalIgnoreCase);
        var start = match > maximumLength / 3 ? Math.Max(0, match - maximumLength / 3) : 0;
        var length = Math.Min(maximumLength, text.Length - start);
        var excerpt = text.Substring(start, length).Replace("\r\n", "\n").Replace('\r', '\n');
        return (start > 0 ? "… " : "") + excerpt + (start + length < text.Length ? " …" : "");
    }

    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var result) ? result : null;

    private static bool IsDeleted(JsonElement element) => element.TryGetProperty("isDeleted", out var value) && value.ValueKind == JsonValueKind.True;

    private static JsonElement Metadata(JsonElement element) =>
        element.TryGetProperty("customData", out var custom) && custom.ValueKind == JsonValueKind.Object &&
        custom.TryGetProperty("pinboard", out var pinboard) && pinboard.ValueKind == JsonValueKind.Object ? pinboard : default;

    private static string? String(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddGroups(JsonElement element, HashSet<string> groups)
    {
        if (!element.TryGetProperty("groupIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return;
        foreach (var id in ids.EnumerateArray())
            if (id.ValueKind == JsonValueKind.String && id.GetString() is { } group) groups.Add(group);
    }

    private static bool SharesGroup(JsonElement element, HashSet<string> groups)
    {
        if (!element.TryGetProperty("groupIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return false;
        return ids.EnumerateArray().Any(id => id.ValueKind == JsonValueKind.String && groups.Contains(id.GetString()!));
    }
}
