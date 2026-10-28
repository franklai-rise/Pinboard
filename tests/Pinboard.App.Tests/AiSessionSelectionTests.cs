using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class AiSessionSelectionTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "PinboardSelectionTests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static SessionDescriptor Item(string id, string source = "Codex", string? parent = null, string? title = null) => new()
    {
        Key = source + ":" + id, Source = source, SourceId = id, Title = title ?? "Conversation " + id,
        ParentKey = parent, UpdatedAt = DateTimeOffset.UtcNow
    };
    private SessionCatalog Catalog() => new(_root);

    [TestMethod]
    public void BulkSelection_QueuesTenThousandIncludingChildrenAndPersists()
    {
        var catalog = Catalog();
        catalog.Merge(Enumerable.Range(0, 10000).Select(i => Item(i.ToString(), parent: i == 1 ? "Codex:0" : null)));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual(10000, catalog.SetSelection(catalog.List().Select(x => x.Key), true));
        var reopened = Catalog();
        Assert.AreEqual(10000, reopened.List().Count(x => x.Selected));
        Assert.IsTrue(reopened.Find("Codex:1")!.Selected);
        Assert.IsNotNull(reopened.NextJob());
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(10), "Bulk writes must not open a connection per row.");
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Objects")), "Selection itself must not read or upload private records.");
    }

    [TestMethod]
    public void DeselectScope_PreservesOtherSelectionsAndExistingVersions()
    {
        var catalog = Catalog(); catalog.Merge([Item("one"), Item("two"), Item("three")]);
        catalog.SetSelection(["Codex:one", "Codex:two", "Codex:three"], true);
        var manifest = new SessionManifest(1, "kept-version", catalog.Find("Codex:one")!.Descriptor, DateTimeOffset.UtcNow, []);
        catalog.AddVersion(manifest, Path.Combine(_root, "kept.json"));
        Assert.AreEqual(2, catalog.SetSelection(["Codex:one", "Codex:two"], false));
        Assert.IsFalse(catalog.Find("Codex:one")!.Selected);
        Assert.IsFalse(catalog.Find("Codex:two")!.Selected);
        Assert.IsTrue(catalog.Find("Codex:three")!.Selected);
        Assert.AreEqual(1, catalog.Versions("Codex:one").Count);
        Assert.IsNotNull(catalog.Find("Codex:one")!.LastBackup);
        Assert.AreEqual("Codex:three", catalog.NextJob());
        catalog.SetSelection(["Codex:three"], false); Assert.IsNull(catalog.NextJob());
    }

    [TestMethod]
    public void RepeatingSelection_PreservesCompletedStatusAndDoesNotRequeue()
    {
        var catalog = Catalog(); catalog.Merge([Item("one")]); catalog.Select("Codex:one", true);
        catalog.SetStatus("Codex:one", "Cloud backup"); catalog.FinishJob("Codex:one");
        Assert.AreEqual(0, catalog.SetSelection(["Codex:one"], true));
        Assert.AreEqual("Cloud backup", catalog.Find("Codex:one")!.Status);
        Assert.IsNull(catalog.NextJob());
    }

    [TestMethod]
    public void FailedBulkEnumeration_RollsBackSelectionAndJobsTogether()
    {
        var catalog = Catalog(); catalog.Merge([Item("one"), Item("two")]);
        Assert.ThrowsException<InvalidOperationException>(() => catalog.SetSelection(FailingKeys(), true));
        Assert.IsTrue(catalog.List().All(x => !x.Selected)); Assert.IsNull(catalog.NextJob());
        static IEnumerable<string> FailingKeys()
        {
            yield return "Codex:one";
            throw new InvalidOperationException("Synthetic enumeration failure");
        }
    }

    [TestMethod]
    public void DuplicateAndUnknownKeys_DoNotCreateExtraJobsOrImplicitOptIn()
    {
        var catalog = Catalog(); catalog.Merge([Item("one")]);
        Assert.AreEqual(1, catalog.SetSelection(["Codex:one", "Codex:one", "Codex:unknown"], true));
        catalog.FinishJob("Codex:one"); catalog.Merge([Item("unknown"), Item("future", parent: "Codex:one")]);
        Assert.IsNull(catalog.NextJob());
        Assert.IsFalse(catalog.Find("Codex:unknown")!.Selected);
        Assert.IsFalse(catalog.Find("Codex:future")!.Selected);
    }

    [TestMethod]
    public void TitleFilter_SelectsMatchingChildNotItsContextParent()
    {
        var catalog = Catalog(); catalog.Merge([Item("parent", title: "Main conversation"),
            Item("child", parent: "Codex:parent", title: "Layout options"), Item("other", title: "Unrelated")]);
        var scope = SessionSelection.Matching(catalog.List(), "  LAYOUT  ", null);
        CollectionAssert.AreEqual(new[] { "Codex:child" }, scope.Select(x => x.Key).ToArray());
        catalog.SetSelection(scope.Select(x => x.Key), true);
        Assert.IsTrue(catalog.Find("Codex:child")!.Selected);
        Assert.IsFalse(catalog.Find("Codex:parent")!.Selected);
        Assert.IsFalse(catalog.Find("Codex:other")!.Selected);
    }

    [TestMethod]
    public void SourceAndTitleFilters_UseDisplayNamesAndDoNotSelectOtherSources()
    {
        var catalog = Catalog(); catalog.Merge([Item("one", title: "Original"), Item("two", "Claude Code", title: "研究笔记")]);
        catalog.Rename("Codex:one", "研究笔记");
        var scope = SessionSelection.Matching(catalog.List(), "研究", "Codex");
        CollectionAssert.AreEqual(new[] { "Codex:one" }, scope.Select(x => x.Key).ToArray());
        Assert.AreEqual(0, SessionSelection.Matching(catalog.List(), "missing", "Codex").Count);
        Assert.AreEqual(0, SessionSelection.Matching(catalog.List(), "Original", null).Count);
    }

    [TestMethod]
    public void EmptyFilters_IncludeFoldedChildrenWithoutRecursivelyOptingInOthers()
    {
        var catalog = Catalog(); catalog.Merge([Item("parent"), Item("child", parent: "Codex:parent"), Item("other", "Claude Code")]);
        Assert.AreEqual(3, SessionSelection.Matching(catalog.List(), "  ", null).Count);
        Assert.AreEqual(2, SessionSelection.Matching(catalog.List(), "", "Codex").Count);
        catalog.Select("Codex:parent", true);
        Assert.IsFalse(catalog.Find("Codex:child")!.Selected, "Individual parent selection must remain independent.");
    }
}
