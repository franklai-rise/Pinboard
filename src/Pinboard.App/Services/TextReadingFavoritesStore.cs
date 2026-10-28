using System.Text.Json;

namespace Pinboard.App.Services;

/// <summary>Profile-only stars. Board data and copied text never enter this file.</summary>
public sealed class TextReadingFavoritesStore(string statePath)
{
    private readonly string _statePath = Path.GetFullPath(statePath);
    private static readonly object ProfileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlySet<string> GetFavorites(string documentPath)
    {
        lock (ProfileLock)
        {
            var profile = ReadProfile();
            return profile.Documents.TryGetValue(NormalizeDocumentPath(documentPath), out var ids)
                ? ids.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public void SetFavorites(string documentPath, IEnumerable<string> elementIds, bool favorite)
    {
        lock (ProfileLock)
        {
            // Reload to retain stars from other reader instances/documents in the same profile.
            var profile = ReadProfile();
            var documentKey = NormalizeDocumentPath(documentPath);
            var favorites = profile.Documents.TryGetValue(documentKey, out var existing)
                ? existing.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in elementIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                if (favorite) favorites.Add(id);
                else favorites.Remove(id);
            }
            if (favorites.Count > 0) profile.Documents[documentKey] = favorites.Order(StringComparer.Ordinal).ToList();
            else profile.Documents.Remove(documentKey);

            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, profile, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _statePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private FavoritesProfile ReadProfile()
    {
        if (!File.Exists(_statePath)) return new FavoritesProfile();
        using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var profile = JsonSerializer.Deserialize<FavoritesProfile>(stream)
            ?? throw new InvalidDataException("The reading favorites profile is empty.");
        if (profile.SchemaVersion != 1 || profile.Documents is null)
            throw new InvalidDataException("The reading favorites profile version is unsupported.");
        profile.Documents = new Dictionary<string, List<string>>(profile.Documents, StringComparer.OrdinalIgnoreCase);
        if (profile.Documents.Values.Any(ids => ids is null))
            throw new InvalidDataException("The reading favorites profile contains invalid records.");
        return profile;
    }

    private static string NormalizeDocumentPath(string path) => Path.GetFullPath(path);

    private sealed class FavoritesProfile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, List<string>> Documents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
