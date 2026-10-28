using Pinboard.App.Models;

namespace Pinboard.App.Services;

public enum SearchResultOrder { Relevance, Recent }

public sealed record SearchResultPage(
    IReadOnlyList<SearchHit> Hits,
    int RetrievedMatchCount,
    bool HasMoreResults,
    bool MayHaveAdditionalMatches,
    int SkippedDocumentCount);

public sealed class SearchService
{
    public const int DefaultResultLimit = 100;
    public const int MaximumResultLimit = 500;

    public async Task<IReadOnlyList<SearchHit>> SearchLibraryAsync(
        string libraryPath,
        string query,
        CancellationToken cancellationToken = default) =>
        (await SearchLibraryPageAsync(libraryPath, query, limit: 200, cancellationToken: cancellationToken)).Hits;

    public async Task<SearchResultPage> SearchLibraryPageAsync(
        string libraryPath,
        string query,
        SearchResultOrder order = SearchResultOrder.Relevance,
        int limit = DefaultResultLimit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = query.Trim();
        if (!Directory.Exists(libraryPath) || query.Length == 0)
        {
            return new([], 0, false, false, 0);
        }

        limit = Math.Clamp(limit, 1, MaximumResultLimit);
        var best = new SortedSet<RankedHit>(new RankedHitComparer(order));
        var retrievedCount = 0;
        var documentLimitReached = false;
        var skippedCount = 0;
        // Keep only the best page, plus one document's bounded search result. File
        // enumeration order therefore affects neither the result order nor memory.
        foreach (var path in Directory.EnumerateFiles(libraryPath, "*.pinboard", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var modifiedAt = File.GetLastWriteTimeUtc(path);
                var hits = await new PinboardDocument(path).SearchAsync(query, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                documentLimitReached |= hits.Count >= 100;
                retrievedCount += hits.Count;
                foreach (var hit in hits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    best.Add(new RankedHit(hit, SearchPresentation.Relevance(hit, query), modifiedAt));
                    if (best.Count > limit) best.Remove(best.Max!);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Report unavailable boards without hiding healthy boards.
                skippedCount++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(best.Select(item => item.Hit).ToArray(), retrievedCount,
            retrievedCount > best.Count || documentLimitReached, documentLimitReached, skippedCount);
    }

    public static IReadOnlyList<SearchHit> OrderDocumentResults(
        IReadOnlyList<SearchHit> hits,
        string query) =>
        hits.OrderByDescending(hit => SearchPresentation.Relevance(hit, query)).ToArray();

    private sealed record RankedHit(SearchHit Hit, int Relevance, DateTime ModifiedAt);

    private sealed class RankedHitComparer(SearchResultOrder order) : IComparer<RankedHit>
    {
        public int Compare(RankedHit? left, RankedHit? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return 1;
            if (right is null) return -1;
            var comparison = order == SearchResultOrder.Relevance
                ? right.Relevance.CompareTo(left.Relevance) : 0;
            if (comparison != 0) return comparison;
            comparison = right.ModifiedAt.CompareTo(left.ModifiedAt);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Hit.DocumentPath, right.Hit.DocumentPath);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Hit.ReferenceId, right.Hit.ReferenceId);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Hit.Kind, right.Hit.Kind);
        }
    }
}
