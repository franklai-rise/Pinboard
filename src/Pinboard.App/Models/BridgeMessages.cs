using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pinboard.App.Models;

public sealed class BridgeMessage
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("documentId")]
    public string? DocumentId { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

public sealed record BridgeFile(string Id, string DataUrl, string? MimeType, long Created);
