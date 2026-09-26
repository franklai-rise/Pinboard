using System.Globalization;
using Pinboard.App.Models;

namespace Pinboard.App.Services;

public enum TextClipsDocumentKind
{
    None,
    Daily,
    Monthly,
    Legacy,
    Manual
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
        var relativePath = Path.GetRelativePath(directory, path);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            return TextClipsDocumentKind.None;
        }

        if (Path.GetDirectoryName(relativePath) is { Length: > 0 })
        {
            return TextClipsDocumentKind.Manual;
        }

        var name = Path.GetFileNameWithoutExtension(relativePath);
        if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return TextClipsDocumentKind.Daily;
        }
        if (DateTime.TryParseExact(name, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return TextClipsDocumentKind.Monthly;
        }
        return TextClipsDocumentKind.Manual;
    }

    public static string CreateManualBoardPath(string libraryPath, string folderName, string title)
    {
        var error = ProjectLibraryService.ValidateBoardTitle(title.Trim());
        if (error is not null) throw new InvalidDataException(error);
        var root = Path.Combine(Path.GetFullPath(libraryPath), AppSettings.TextClipsDirectoryName);
        var folder = ResolveManualFolderPath(root, folderName);
        Directory.CreateDirectory(folder);
        return CreateUniquePath(folder, title.Trim() + ".pinboard");
    }

    public static string CreateFolder(string libraryPath, string folderName)
    {
        var root = Path.Combine(Path.GetFullPath(libraryPath), AppSettings.TextClipsDirectoryName);
        var name = folderName.Trim();
        if (string.IsNullOrWhiteSpace(name) || ProjectLibraryService.ValidateBoardTitle(name) is not null)
        {
            throw new InvalidDataException(LocalizationService.T("TextFolderInvalid"));
        }
        var path = ResolveManualFolderPath(root, name);
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException(LocalizationService.T("TextFolderExistsFormat", name));
        }
        Directory.CreateDirectory(path);
        return name;
    }

    public static IReadOnlyList<string> GetManualFolders(string libraryPath)
    {
        var root = Path.Combine(Path.GetFullPath(libraryPath), AppSettings.TextClipsDirectoryName);
        Directory.CreateDirectory(root);
        return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string MoveManualBoard(string libraryPath, string sourcePath, string folderName)
    {
        if (GetDocumentKind(libraryPath, sourcePath) != TextClipsDocumentKind.Manual)
            throw new InvalidOperationException(LocalizationService.T("BoardOutsideLibrary"));
        var root = Path.Combine(Path.GetFullPath(libraryPath), AppSettings.TextClipsDirectoryName);
        var folder = ResolveManualFolderPath(root, folderName);
        if (Path.GetDirectoryName(Path.GetFullPath(sourcePath))!.Equals(folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.T("BoardAlreadyInProject"));
        Directory.CreateDirectory(folder);
        var target = CreateUniquePath(folder, Path.GetFileName(sourcePath));
        File.Move(sourcePath, target);
        return target;
    }

    private static string CreateUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} {index}{extension}");
            index++;
        }
        return candidate;
    }

    private static string ResolveManualFolderPath(string root, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return root;
        }

        var name = folderName.Trim();
        if (ProjectLibraryService.ValidateBoardTitle(name) is not null
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
            || name is "." or "..")
        {
            throw new InvalidDataException(LocalizationService.T("TextFolderInvalid"));
        }

        var folder = Path.GetFullPath(Path.Combine(root, name));
        var relative = Path.GetRelativePath(root, folder);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(LocalizationService.T("TextFolderInvalid"));
        }
        return folder;
    }
}
