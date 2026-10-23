using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public record LogConfig
{
    [JsonPropertyName("level")] public string? Level { get; init; }
    [JsonPropertyName("timestamp")] public bool? Timestamp { get; init; }
}
