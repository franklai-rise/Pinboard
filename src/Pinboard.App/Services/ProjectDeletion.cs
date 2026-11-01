using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed record ProjectDeletionPlan(string ProjectName, string? DirectoryPath,
    IReadOnlyList<string> Files, IReadOnlyList<string> BoardPaths)
{
    public string? LibraryPath { get; init; }
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        return DirectoryPath is { } directory
            ? full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            : LibraryPath is { } root
                ? Path.GetExtension(full).Equals(".pinboard", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
                    && TextClipsLibrary.GetDocumentKind(root, full) == TextClipsDocumentKind.None
                    && ScreenshotBoardsLibrary.GetDocumentKind(root, full) == ScreenshotDocumentKind.None
                : Files.Contains(full, StringComparer.OrdinalIgnoreCase);
    }
}

public static class ProjectDeletion
{
    public static ProjectDeletionPlan Prepare(string libraryPath, string projectName)
    {
        var root = Path.GetFullPath(libraryPath).TrimEnd(Path.DirectorySeparatorChar);
        var error = ProjectLibraryService.ValidateProjectName(projectName, allowDefaultProject: true);
        if (error is not null) throw new InvalidOperationException(error);
        if (projectName.Equals(ProjectLibraryService.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
        {
            // The default project is virtual: the library root is NEVER a directory deletion target.
            var boards = Directory.EnumerateFiles(root, "*.pinboard", SearchOption.TopDirectoryOnly)
                .Where(path => TextClipsLibrary.GetDocumentKind(root, path) == TextClipsDocumentKind.None
                    && ScreenshotBoardsLibrary.GetDocumentKind(root, path) == ScreenshotDocumentKind.None)
                .Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var file in boards) RejectReparsePoint(file);
            return new(projectName, null, boards, boards) { LibraryPath = root };
        }
        var directory = Path.GetFullPath(Path.Combine(root, projectName));
        if (!Path.GetDirectoryName(directory)!.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Project must be an immediate child of the library.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var next))
        {
            RejectReparsePoint(next);
            foreach (var entry in Directory.EnumerateFileSystemEntries(next))
            {
                RejectReparsePoint(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else files.Add(Path.GetFullPath(entry));
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return new(projectName, directory, files.ToArray(),
            files.Where(path => Path.GetExtension(path).Equals(".pinboard", StringComparison.OrdinalIgnoreCase)).ToArray()) { LibraryPath = root };
    }

    public static void Execute(string libraryPath, ProjectDeletionPlan confirmed,
        Action<string> recycleFile, Action<string> recycleDirectory)
    {
        // Recheck the scope immediately before recycling. A file added while the
        // confirmation was open requires a new confirmation, never a wider deletion.
        var current = Prepare(libraryPath, confirmed.ProjectName);
        if (!string.Equals(current.DirectoryPath, confirmed.DirectoryPath, StringComparison.OrdinalIgnoreCase)
            || !current.Files.SequenceEqual(confirmed.Files, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.CurrentLanguage == LocalizationService.Chinese
                ? "项目内容已变化，请重新打开删除确认。" : "Project contents changed. Please confirm deletion again.");
        if (current.DirectoryPath is { } directory) recycleDirectory(directory);
        else foreach (var file in current.Files) recycleFile(file);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(LocalizationService.CurrentLanguage == LocalizationService.Chinese
                ? "项目包含链接或联接目录，无法整体删除。请先在资源管理器中检查。"
                : "This project contains a link or junction. Inspect it in File Explorer before deleting it.");
    }
}
