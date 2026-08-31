namespace Pinboard.App.Models;

public sealed record DocumentCheckpointInfo(
    long Revision,
    DateTimeOffset SavedAt,
    string Reason,
    long StoredBytes);

public sealed record DocumentOptimizeResult(
    int RemovedFileMappings,
    int RemovedAssets,
    long BytesBefore,
    long BytesAfter,
    bool Vacuumed);
