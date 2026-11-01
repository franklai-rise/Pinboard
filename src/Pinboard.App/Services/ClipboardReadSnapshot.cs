namespace Pinboard.App.Services;

public enum ClipboardReadState { Captured, Ignored, Changed, Busy, Failed }

public sealed record ClipboardReadSnapshot<T>(ClipboardReadState State, uint Sequence, DateTimeOffset CapturedAt, T? Value);

/// <summary>Associates content with one stable clipboard version, never a later version read after it.</summary>
public static class ClipboardSnapshotReader
{
    public static ClipboardReadSnapshot<T> Read<T>(
        uint expectedSequence,
        DateTimeOffset capturedAt,
        Func<uint> getSequence,
        Func<T?> read,
        Func<T, bool> hasContent,
        Func<Exception, bool> isTransientBusy)
    {
        var before = getSequence();
        if (before == 0 || before != expectedSequence)
            return new(ClipboardReadState.Changed, before, capturedAt, default);
        T? value;
        try { value = read(); }
        catch (Exception error)
        {
            var afterError = getSequence();
            if (afterError != before) return new(ClipboardReadState.Changed, afterError, capturedAt, default);
            return new(isTransientBusy(error) ? ClipboardReadState.Busy : ClipboardReadState.Failed,
                before, capturedAt, default);
        }
        var after = getSequence();
        if (after != before) return new(ClipboardReadState.Changed, after, capturedAt, default);
        return value is not null && hasContent(value)
            ? new(ClipboardReadState.Captured, before, capturedAt, value)
            : new(ClipboardReadState.Ignored, before, capturedAt, default);
    }
}

public static class ClipboardTextCapturePolicy
{
    public const int MaximumTextLength = 100_000;
    public const string TruncatedMarker = "\n\n[Pinboard: clipboard text was truncated at 100,000 characters.]";

    public static string Truncate(string text) => text.Length <= MaximumTextLength
        ? text
        : text[..(MaximumTextLength - TruncatedMarker.Length)] + TruncatedMarker;

    public static bool IsDrawingPayload(string text)
    {
        // Excalidraw puts its type and elements header before embedded image data.
        // Inspect only a bounded prefix, never decode or parse megabytes of base64.
        var prefix = text.AsSpan().TrimStart();
        if (prefix.IsEmpty || prefix[0] != '{') return false;
        var bytes = System.Text.Encoding.UTF8.GetBytes(prefix[..Math.Min(prefix.Length, 16_384)].ToString());
        var reader = new System.Text.Json.Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        var drawingType = false;
        var elements = false;
        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth != 1 || reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) continue;
                var typeProperty = reader.ValueTextEquals("type");
                var elementsProperty = reader.ValueTextEquals("elements");
                if (!reader.Read()) return false;
                if (typeProperty && reader.TokenType == System.Text.Json.JsonTokenType.String)
                    drawingType = reader.ValueTextEquals("excalidraw/clipboard");
                if (elementsProperty && reader.TokenType == System.Text.Json.JsonTokenType.StartArray) elements = true;
                if (drawingType && elements) return true;
                if (reader.TokenType is System.Text.Json.JsonTokenType.StartArray or System.Text.Json.JsonTokenType.StartObject)
                    if (!reader.TrySkip()) return false;
            }
        }
        catch (System.Text.Json.JsonException) { }
        return false;
    }
}
