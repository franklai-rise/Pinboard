namespace Pinboard.App.Models;

public sealed record AssetRecord(
    string Hash,
    string MimeType,
    int Width,
    int Height,
    byte[] Bytes,
    DateTimeOffset CreatedAt);

public sealed record AssetPayload(
    string FileId,
    string Hash,
    string MimeType,
    int Width,
    int Height,
    string DataUrl,
    long CreatedAt);

public sealed record DocumentSnapshot(
    string Path,
    string Title,
    string SceneJson,
    IReadOnlyList<AssetPayload> Files,
    long Revision);

public sealed record SearchHit(
    string DocumentPath,
    string DocumentTitle,
    string Kind,
    string ReferenceId,
    string Text,
    double X,
    double Y);

public sealed record InsertResult(
    string ElementId,
    string FileId,
    string AssetHash,
    double X,
    double Y,
    double Width,
    double Height,
    string SceneJson);

public sealed record TextClipInsertResult(
    string ElementId,
    double X,
    double Y,
    double Width,
    double Height,
    string SceneJson);

public sealed record ImportReport(
    string SourcePath,
    string DestinationPath,
    int ElementCount,
    int ImageCount,
    int PlaceholderCount,
    IReadOnlyList<string> Warnings);
