using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed class ProjectLibraryService
{
    public const string DefaultProjectName = "默认项目";
    public const string OtherLocationProjectName = "其他位置";
    /// <summary>
    /// Internal project used for boards the user has archived. It is deliberately
    /// not offered as a normal project in creation or move dialogs.
    /// </summary>
    public const string ArchiveProjectName = "_Archive";

    public ProjectLibraryService(string libraryPath)
    {
        LibraryPath = Path.GetFullPath(libraryPath);
    }

    public string LibraryPath { get; }

    public IReadOnlyList<string> GetProjectNames()
    {
        Directory.CreateDirectory(LibraryPath);
        var projects = Directory.EnumerateDirectories(LibraryPath, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name)
                && !name.Equals(ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase)
                && !name.Equals(AppSettings.TextClipsDirectoryName, StringComparison.CurrentCultureIgnoreCase)
                && !name.Equals(AppSettings.ScreenshotsDirectoryName, StringComparison.CurrentCultureIgnoreCase))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        projects.Insert(0, DefaultProjectName);
        return projects;
    }

    public string CreateProject(string projectName)
    {
        projectName = projectName.Trim();
        var error = ValidateProjectName(projectName, allowDefaultProject: false);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }

        var directory = Path.Combine(LibraryPath, projectName);
        if (Directory.Exists(directory))
        {
            throw new InvalidOperationException(LocalizationService.T("ProjectExistsFormat", projectName));
        }
        Directory.CreateDirectory(directory);
        return projectName;
    }

    public string CreateBoardPath(string title, string projectName)
    {
        title = title.Trim();
        projectName = string.IsNullOrWhiteSpace(projectName) ? DefaultProjectName : projectName.Trim();
        var titleError = ValidateBoardTitle(title);
        if (titleError is not null)
        {
            throw new InvalidDataException(titleError);
        }
        var projectError = ValidateProjectName(projectName, allowDefaultProject: true);
        if (projectError is not null)
        {
            throw new InvalidDataException(projectError);
        }

        var directory = GetProjectDirectory(projectName);
        Directory.CreateDirectory(directory);
        return CreateUniquePath(directory, title + ".pinboard");
    }

    public string MoveBoard(string sourcePath, string projectName)
    {
        return MoveBoardCore(sourcePath, projectName, allowArchiveProject: false);
    }

    public string ArchiveBoard(string sourcePath)
    {
        return MoveBoardCore(sourcePath, ArchiveProjectName, allowArchiveProject: true);
    }

    private string MoveBoardCore(string sourcePath, string projectName, bool allowArchiveProject)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        projectName = projectName.Trim();
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(LocalizationService.T("BoardMoveSourceMissing"), sourcePath);
        }
        if (GetProjectName(sourcePath).Equals(OtherLocationProjectName, StringComparison.CurrentCultureIgnoreCase))
        {
            throw new InvalidOperationException(LocalizationService.T("BoardOutsideLibrary"));
        }
        if (!allowArchiveProject || !projectName.Equals(ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase))
        {
            var projectError = ValidateProjectName(projectName, allowDefaultProject: true);
            if (projectError is not null)
            {
                throw new InvalidDataException(projectError);
            }
        }

        var destinationDirectory = GetProjectDirectory(projectName);
        Directory.CreateDirectory(destinationDirectory);
        if (Path.GetFullPath(Path.GetDirectoryName(sourcePath)!)
            .Equals(Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(LocalizationService.T("BoardAlreadyInProject"));
        }

        var destination = CreateUniquePath(destinationDirectory, Path.GetFileName(sourcePath));
        File.Move(sourcePath, destination);
        return destination;
    }

    public string GetProjectName(string filePath)
    {
        var libraryPath = LibraryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(filePath);
        var relativePath = Path.GetRelativePath(libraryPath, fullPath);
        if (relativePath.Equals(Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            return DefaultProjectName;
        }
        if (relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.Equals("..", StringComparison.Ordinal))
        {
            return OtherLocationProjectName;
        }

        var separatorIndex = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separatorIndex > 0 ? relativePath[..separatorIndex] : DefaultProjectName;
    }

    public static string? ValidateProjectName(string projectName, bool allowDefaultProject)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return LocalizationService.T("ProjectNameRequired");
        }
        if (allowDefaultProject && projectName.Equals(DefaultProjectName, StringComparison.CurrentCultureIgnoreCase))
        {
            return null;
        }
        if (projectName.Equals(DefaultProjectName, StringComparison.CurrentCultureIgnoreCase)
            || projectName.Equals(OtherLocationProjectName, StringComparison.CurrentCultureIgnoreCase)
            || projectName.Equals(ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase)
            || projectName.Equals(AppSettings.TextClipsDirectoryName, StringComparison.CurrentCultureIgnoreCase)
            || projectName.Equals(AppSettings.ScreenshotsDirectoryName, StringComparison.CurrentCultureIgnoreCase))
        {
            return LocalizationService.T("ProjectNameReserved");
        }
        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || projectName.Contains(Path.DirectorySeparatorChar)
            || projectName.Contains(Path.AltDirectorySeparatorChar))
        {
            return LocalizationService.T("ProjectNameInvalidChars");
        }
        if (projectName.EndsWith('.') || projectName.EndsWith(' ') || IsReservedWindowsName(projectName))
        {
            return LocalizationService.T("ProjectNameWindowsInvalid");
        }
        return null;
    }

    public static string? ValidateBoardTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return LocalizationService.T("BoardNameRequired");
        }
        if (title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return LocalizationService.T("BoardNameInvalidChars");
        }
        if (title.EndsWith('.') || title.EndsWith(' ') || IsReservedWindowsName(title))
        {
            return LocalizationService.T("BoardNameWindowsInvalid");
        }
        return null;
    }

    private string GetProjectDirectory(string projectName)
    {
        return projectName.Equals(DefaultProjectName, StringComparison.CurrentCultureIgnoreCase)
            ? LibraryPath
            : projectName.Equals(ArchiveProjectName, StringComparison.CurrentCultureIgnoreCase)
                ? Path.Combine(LibraryPath, ArchiveProjectName)
            : Path.Combine(LibraryPath, projectName);
    }

    private static string CreateUniquePath(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
        }
        return candidate;
    }

    private static bool IsReservedWindowsName(string name)
    {
        var stem = name.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
    }
}
