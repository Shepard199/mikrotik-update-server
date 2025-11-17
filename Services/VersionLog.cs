using System.Text.Json.Serialization;

namespace MikroTik.UpdateServer.Services;

public class VersionLog
{
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }

    [JsonPropertyName("v6Stable")] public string V6Stable { get; set; } = string.Empty;

    [JsonPropertyName("v7Fixed")] public string V7Fixed { get; set; } = string.Empty;

    [JsonPropertyName("v7Stable")] public string V7Stable { get; set; } = string.Empty;
}