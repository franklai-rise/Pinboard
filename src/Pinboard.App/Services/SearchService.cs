using Pinboard.App.Models;

namespace Pinboard.App.Services;

public sealed class SearchService
{
    public async Task<IReadOnlyList<SearchHit>> SearchLibraryAsync(
        string libraryPath,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(libraryPath) || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var results = new List<SearchHit>();
        foreach (var path in Directory.EnumerateFiles(libraryPath, "*.pinboard", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = new PinboardDocument(path);
                results.AddRange(await document.SearchAsync(query, cancellationToken));
            }
            catch
            {
                // One damaged or locked board must not hide results from the others.
            }
        }

        return results.Take(200).ToList();
    }
}
