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
}
