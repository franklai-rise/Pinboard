using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class SearchExperienceTests
{
    [TestMethod]
    public void Excerpt_CentersLateKeywordAndHighlightsAllVisibleMatches()
    {
        var text = new string('前', 1800) + "推进器记录：推进器已修复。" + new string('后', 500);
        var excerpt = SearchPresentation.BuildExcerpt(text, "推进器");

        Assert.IsTrue(excerpt.Text.StartsWith('…'));
        Assert.IsTrue(excerpt.Text.EndsWith('…'));
        Assert.AreEqual(2, excerpt.Runs.Count(run => run.IsMatch));
        Assert.IsTrue(excerpt.Runs.Where(run => run.IsMatch).All(run => run.Text == "推进器"));
        Assert.IsTrue(excerpt.Text.Length <= 242);
    }

    [TestMethod]
    public void Excerpt_CollapsesLineBreaksAndMatchesCaseInsensitively()
    {
        var excerpt = SearchPresentation.BuildExcerpt("Before\r\n  TARGET\tword\n and target word after", "target word");

        Assert.AreEqual("Before TARGET word and target word after", excerpt.Text);
        Assert.AreEqual(2, excerpt.Runs.Count(run => run.IsMatch));
    }

    [TestMethod]
    public void Excerpt_RemainsBoundedForVeryLongQueryAndSafeAtSurrogateBoundary()
    {
        var query = new string('q', 600);
        var excerpt = SearchPresentation.BuildExcerpt(new string('a', 159) + "😀" + query + " end", query);

        Assert.IsTrue(excerpt.Text.Length <= 244);
        Assert.IsTrue(excerpt.Runs.Any(run => run.IsMatch));
        Assert.IsFalse(excerpt.Text.Contains('\uFFFD'));
    }

    [TestMethod]
    public void SearchRequests_CancelOlderQueryAndRejectOutOfOrderCompletion()
    {
        using var requests = new SearchRequestController();
        using var lifetime = new CancellationTokenSource();
        using var older = requests.Begin("first immutable query", lifetime.Token);
        using var current = requests.Begin("latest query", lifetime.Token);

        Assert.IsTrue(older.Token.IsCancellationRequested);
        Assert.IsFalse(requests.IsCurrent(older));
        Assert.IsTrue(requests.IsCurrent(current));
        Assert.AreEqual("first immutable query", older.Query);
        older.Dispose();
        Assert.IsTrue(requests.IsCurrent(current), "Finishing the old request must not clear the new request.");
        lifetime.Cancel();
        Assert.IsTrue(current.Token.IsCancellationRequested);
        Assert.IsFalse(requests.IsCurrent(current));
    }

    [TestMethod]
    public void SearchRequests_DisposeCancelsActiveRequest()
    {
        var requests = new SearchRequestController();
        using var current = requests.Begin("query");
        requests.Dispose();

        Assert.IsTrue(current.Token.IsCancellationRequested);
        Assert.IsFalse(requests.IsCurrent(current));
        Assert.ThrowsExactly<ObjectDisposedException>(() => requests.Begin("after close"));
    }

    [TestMethod]
    public async Task SearchLibrary_RanksAcrossAllBoardsAndSupportsRecentOrder()
    {
        using var temp = new TemporaryDirectory();
        var older = await CreateTextBoardAsync(temp.Path, "z-old", "distinctive search phrase");
        var newer = await CreateTextBoardAsync(temp.Path, "a-new", "Some context before distinctive search phrase with notes");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = new SearchService();

        var relevance = await service.SearchLibraryPageAsync(temp.Path, "distinctive search phrase", limit: 1);
        var recent = await service.SearchLibraryPageAsync(temp.Path, "distinctive search phrase", SearchResultOrder.Recent, limit: 1);

        Assert.AreEqual(older, relevance.Hits.Single().DocumentPath);
        Assert.AreEqual(newer, recent.Hits.Single().DocumentPath);
        Assert.AreEqual(2, relevance.RetrievedMatchCount);
        Assert.IsTrue(relevance.HasMoreResults);
        Assert.IsFalse(relevance.MayHaveAdditionalMatches);
    }

    [TestMethod]
    public async Task SearchLibrary_ReportsPerBoardLimitRatherThanSilentlyTruncating()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "many.pinboard");
        var board = new PinboardDocument(path);
        for (var index = 0; index < 105; index++)
            await board.InsertCapturedTextAsync($"searchable phrase {index}", "test");

        var page = await new SearchService().SearchLibraryPageAsync(temp.Path, "searchable phrase", limit: 20);

        Assert.AreEqual(20, page.Hits.Count);
        Assert.AreEqual(100, page.RetrievedMatchCount);
        Assert.IsTrue(page.HasMoreResults);
        Assert.IsTrue(page.MayHaveAdditionalMatches);
    }

    [TestMethod]
    public async Task SearchLibrary_ReportsDamagedBoardWithoutHidingHealthyBoard()
    {
        using var temp = new TemporaryDirectory();
        await CreateTextBoardAsync(temp.Path, "healthy", "recoverable phrase");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "damaged.pinboard"), "not a SQLite database");

        var page = await new SearchService().SearchLibraryPageAsync(temp.Path, "recoverable phrase");

        Assert.AreEqual(1, page.Hits.Count);
        Assert.AreEqual(1, page.SkippedDocumentCount);
    }

    [TestMethod]
    public async Task SearchLibrary_PropagatesCancellationEvenForEmptyLibrary()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SearchService().SearchLibraryPageAsync(temp.Path, "query", cancellationToken: cancellation.Token));
    }

    private static async Task<string> CreateTextBoardAsync(string directory, string name, string text)
    {
        var path = Path.Combine(directory, name + ".pinboard");
        await new PinboardDocument(path).InsertCapturedTextAsync(text, "test");
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PinboardSearchTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
