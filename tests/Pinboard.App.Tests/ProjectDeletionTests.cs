using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public class ProjectDeletionTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Pinboard-delete-test-" + Guid.NewGuid().ToString("N"))).FullName;
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string FileAt(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic test fixture");
        return path;
    }

    [TestMethod]
    public void DefaultProject_NeverIncludesLibraryRootOrAutomaticBoards()
    {
        var board = FileAt("Notes.pinboard");
        FileAt("2026-09.pinboard"); FileAt("Text Clips.pinboard");
        FileAt(Path.Combine("Screenshots", "2026-11-01.pinboard"));
        FileAt(Path.Combine("Text Clips", "2026-11-01.pinboard"));
        FileAt(Path.Combine("Research", "Study.pinboard"));
        var plan = ProjectDeletion.Prepare(_root, ProjectLibraryService.DefaultProjectName);
        Assert.IsNull(plan.DirectoryPath);
        CollectionAssert.AreEqual(new[] { board }, plan.Files.ToArray());
        var recycled = new List<string>();
        ProjectDeletion.Execute(_root, plan, recycled.Add, _ => Assert.Fail("Must never recycle the library root."));
        CollectionAssert.AreEqual(new[] { board }, recycled);
        Assert.IsFalse(plan.Contains(Path.Combine(_root, "Screenshots", "2026-11-01.pinboard")));
        Assert.IsTrue(plan.Contains(Path.Combine(_root, "Future.pinboard")), "Queued, not-yet-created targets still belong to the default project.");
        Assert.IsFalse(plan.Contains(Path.Combine(_root, "2026-09.pinboard")));
        Assert.IsFalse(plan.Contains(Path.Combine(_root, "keep.txt")));
    }

    [TestMethod]
    public void RealProject_ListsAllFilesAndDoesNotMatchSiblingPrefix()
    {
        FileAt(Path.Combine("Research", "Study.pinboard"));
        FileAt(Path.Combine("Research", "extra.txt"));
        var sibling = FileAt(Path.Combine("Research-old", "Other.pinboard"));
        var plan = ProjectDeletion.Prepare(_root, "Research");
        Assert.AreEqual(2, plan.Files.Count);
        Assert.AreEqual(1, plan.BoardPaths.Count);
        Assert.IsFalse(plan.Contains(sibling));
        var recycled = "";
        ProjectDeletion.Execute(_root, plan, _ => Assert.Fail("Recycle the project as one folder."), path => recycled = path);
        Assert.AreEqual(Path.Combine(_root, "Research"), recycled);
    }

    [TestMethod]
    public void ChangedProject_RequiresAnotherConfirmation()
    {
        FileAt(Path.Combine("Research", "Study.pinboard"));
        var plan = ProjectDeletion.Prepare(_root, "Research");
        FileAt(Path.Combine("Research", "new.txt"));
        Assert.ThrowsException<InvalidOperationException>(() => ProjectDeletion.Execute(_root, plan,
            _ => Assert.Fail("No file may be recycled."), _ => Assert.Fail("No folder may be recycled.")));
    }

    [TestMethod]
    public void EmptyProject_CanBeRecycledWithoutDeletingOtherProjects()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Empty"));
        var plan = ProjectDeletion.Prepare(_root, "Empty");
        Assert.AreEqual(0, plan.Files.Count);
        string? target = null;
        ProjectDeletion.Execute(_root, plan, _ => Assert.Fail(), path => target = path);
        Assert.AreEqual(Path.Combine(_root, "Empty"), target);
    }

    [TestMethod]
    public void ReservedAndEscapingProjects_AreRejected()
    {
        foreach (var name in new[] { "..", "../outside", "Screenshots", "Text Clips", "_Archive", "其他位置" })
            Assert.ThrowsException<InvalidOperationException>(() => ProjectDeletion.Prepare(_root, name));
    }

    [TestMethod]
    public void RecycleFailure_LeavesSourceAndReportsFailure()
    {
        var board = FileAt(Path.Combine("Research", "Study.pinboard"));
        var plan = ProjectDeletion.Prepare(_root, "Research");
        Assert.ThrowsException<IOException>(() => ProjectDeletion.Execute(_root, plan,
            _ => Assert.Fail(), _ => throw new IOException("synthetic file lock")));
        Assert.IsTrue(File.Exists(board));
    }

    [TestMethod]
    public void ForgedDeletionPlan_CannotBroadenItsProjectScope()
    {
        var own = FileAt(Path.Combine("Research", "Study.pinboard"));
        var unrelated = FileAt(Path.Combine("Other", "Keep.pinboard"));
        var confirmed = ProjectDeletion.Prepare(_root, "Research") with { Files = new[] { own, unrelated } };
        Assert.ThrowsException<InvalidOperationException>(() => ProjectDeletion.Execute(_root, confirmed,
            _ => Assert.Fail("No file may be recycled."), _ => Assert.Fail("No directory may be recycled.")));
        Assert.IsTrue(File.Exists(unrelated));
    }

    [TestMethod]
    public void LinkedProject_IsRejectedWithoutFollowingItsTarget()
    {
        var preserved = FileAt(Path.Combine("Other", "Keep.pinboard"));
        try { Directory.CreateSymbolicLink(Path.Combine(_root, "Linked"), Path.Combine(_root, "Other")); }
        catch (Exception error) when (error is UnauthorizedAccessException
            || error is IOException && (error.HResult & 0xffff) == 1314)
        { Assert.Inconclusive("Symbolic link creation requires Windows privileges."); }
        Assert.ThrowsException<InvalidOperationException>(() => ProjectDeletion.Prepare(_root, "Linked"));
        Assert.IsTrue(File.Exists(preserved));
    }
}
