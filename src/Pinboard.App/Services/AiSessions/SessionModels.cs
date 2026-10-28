using System.Text.Json;

namespace Pinboard.App.Services.AiSessions;

public sealed record SessionDescriptor
{
    public required string Key { get; init; }
    public required string Source { get; init; }
    public required string SourceId { get; init; }
    public required string Title { get; init; }
    public string Project { get; init; } = "";
    public string? ParentKey { get; init; }
    public string? Path { get; init; }
    public string? MetadataPath { get; init; }
    public string? HistoryPath { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public long Size { get; init; }
    public bool PreviewTitle { get; init; }
    public bool Archived { get; init; }
    public string Availability { get; init; } = "Available";
}

public sealed record SessionEntry(SessionDescriptor Descriptor, bool Selected, string? DisplayName,
    string Status, string? Error, DateTimeOffset? LastBackup)
{
    public string Key => Descriptor.Key;
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Descriptor.Title : DisplayName;
}

public sealed record SnapshotFile(string RelativePath, string Path, bool IncompleteAttachments = false, bool IncompleteSource = false);
public sealed record ArchiveChunk(string Hash, int Length);
public sealed record ArchiveFile(string RelativePath, long Length, string Sha256, IReadOnlyList<ArchiveChunk> Chunks);
public sealed record SessionManifest(int SchemaVersion, string VersionId, SessionDescriptor Session,
    DateTimeOffset CreatedAt, IReadOnlyList<ArchiveFile> Files, IReadOnlyList<string>? Warnings = null);
public sealed record SessionVersion(string Id, string SessionKey, DateTimeOffset CreatedAt,
    string ManifestPath, long Size, string? CloudAccount, string? CloudFileId);
public sealed record RemoteObject(string Hash, string FileId, string? UploadUri, long Offset);
public sealed record SessionJobInfo(int Attempts, DateTimeOffset DueAt, bool Blocked, SessionFailureKind FailureKind);
public sealed record DriveAccount(string Id, string DisplayName, long? Limit, long Usage)
{
    public long? Available => Limit is { } limit ? Math.Max(0, limit - Usage) : null;
}

public interface ISessionSourceAdapter
{
    string Name { get; }
    Task<IReadOnlyList<SessionDescriptor>> DiscoverAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SnapshotFile>> SnapshotAsync(SessionDescriptor session, string stagingDirectory,
        CancellationToken cancellationToken);
}

public interface ISessionCloud
{
    bool IsConnected { get; }
    string? ConfirmedAccountId => null;
    Task<DriveAccount> GetAccountAsync(CancellationToken cancellationToken);
    Task<string> UploadObjectAsync(string hash, string path, CancellationToken cancellationToken);
    Task<string> UploadManifestAsync(SessionManifest manifest, string path, CancellationToken cancellationToken);
    Task DownloadObjectAsync(string fileId, string destination, CancellationToken cancellationToken);
    Task<string?> FindObjectAsync(string hash, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    Task<IReadOnlyList<(string FileId, string Hash)>> ListManifestsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<(string, string)>>([]);
}

public static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public static string ShortTitle(string text) => string.Join(" ", text.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).Trim() is var title && title.Length > 100 ? title[..100] + "…" : title;
}
