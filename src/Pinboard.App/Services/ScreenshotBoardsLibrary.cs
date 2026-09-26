using System.Globalization;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public enum ScreenshotDocumentKind
{
    None,
    Daily,
    History
}

public static class ScreenshotBoardsLibrary
{
    public static ScreenshotDocumentKind GetDocumentKind(string libraryPath, string filePath)
    {
        var library = Path.GetFullPath(libraryPath);
        var path = Path.GetFullPath(filePath);
        var dailyDirectory = Path.Combine(library, AppSettings.ScreenshotsDirectoryName);
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);

        if (parent is not null && parent.Equals(dailyDirectory, StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return ScreenshotDocumentKind.Daily;
        }

        return parent is not null && parent.Equals(library, StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParseExact(name, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? ScreenshotDocumentKind.History
            : ScreenshotDocumentKind.None;
    }
}
