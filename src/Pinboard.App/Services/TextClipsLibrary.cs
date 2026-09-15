using System.Globalization;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public enum TextClipsDocumentKind
{
    None,
    Monthly,
    Legacy
}

public static class TextClipsLibrary
{
    public static TextClipsDocumentKind GetDocumentKind(string libraryPath, string filePath)
    {
        var library = Path.GetFullPath(libraryPath);
        var path = Path.GetFullPath(filePath);
        var legacy = Path.Combine(library, AppSettings.TextClipsBoardFileName);
        if (path.Equals(legacy, StringComparison.OrdinalIgnoreCase))
        {
            return TextClipsDocumentKind.Legacy;
        }

        var directory = Path.Combine(library, AppSettings.TextClipsDirectoryName);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !parent.Equals(directory, StringComparison.OrdinalIgnoreCase))
        {
            return TextClipsDocumentKind.None;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return DateTime.TryParseExact(name, "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _)
            ? TextClipsDocumentKind.Monthly
            : TextClipsDocumentKind.None;
    }
}
