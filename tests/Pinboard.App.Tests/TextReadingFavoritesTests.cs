using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class TextReadingFavoritesTests
{
    [TestMethod]
    public void Favorites_PersistIndependentlyForEachDocumentWithoutChangingBoards()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "profile", "reading-favorites.json");
        var firstBoard = Path.Combine(directory.Path, "first.pinboard");
        var secondBoard = Path.Combine(directory.Path, "second.pinboard");
        File.WriteAllText(firstBoard, "original first board");
        File.WriteAllText(secondBoard, "original second board");
        var store = new TextReadingFavoritesStore(statePath);
        store.SetFavorites(firstBoard, ["clip-1", "clip-2"], true);
        store.SetFavorites(secondBoard, ["clip-1"], true);

        var reloaded = new TextReadingFavoritesStore(statePath);
        CollectionAssert.AreEquivalent(new[] { "clip-1", "clip-2" }, reloaded.GetFavorites(firstBoard).ToArray());
        CollectionAssert.AreEquivalent(new[] { "clip-1" }, reloaded.GetFavorites(secondBoard).ToArray());
        reloaded.SetFavorites(firstBoard.ToUpperInvariant(), ["clip-1"], false);
        CollectionAssert.AreEqual(new[] { "clip-2" }, reloaded.GetFavorites(firstBoard).ToArray());
        Assert.AreEqual("original first board", File.ReadAllText(firstBoard));
        Assert.AreEqual("original second board", File.ReadAllText(secondBoard));
        Assert.IsFalse(File.ReadAllText(statePath).Contains("original first board", StringComparison.Ordinal));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(statePath)!, "*.tmp").Length);
    }

    [TestMethod]
    public void Favorites_MultipleStoreInstancesRetainOtherDocumentsAndDeduplicateIds()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "favorites.json");
        var first = new TextReadingFavoritesStore(statePath);
        var second = new TextReadingFavoritesStore(statePath);
        var boardA = Path.Combine(directory.Path, "A.pinboard");
        var boardB = Path.Combine(directory.Path, "B.pinboard");
        first.SetFavorites(boardA, ["duplicate", "duplicate", ""], true);
        second.SetFavorites(boardB, ["other"], true);
        first.SetFavorites(boardA, ["added"], true);

        CollectionAssert.AreEquivalent(new[] { "duplicate", "added" }, second.GetFavorites(boardA).ToArray());
        CollectionAssert.AreEqual(new[] { "other" }, first.GetFavorites(boardB).ToArray());
    }

    [TestMethod]
    public void Favorites_InvalidProfileIsNeverOverwritten()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "favorites.json");
        const string original = "{invalid, recoverable profile}";
        File.WriteAllText(statePath, original);
        var store = new TextReadingFavoritesStore(statePath);

        Assert.ThrowsException<System.Text.Json.JsonException>(() => store.SetFavorites(Path.Combine(directory.Path, "board.pinboard"), ["clip"], true));
        Assert.AreEqual(original, File.ReadAllText(statePath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinboardReaderTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
