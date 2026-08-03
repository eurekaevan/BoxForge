using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public record HttpClientConfig
{
    [JsonPropertyName("tag")] public required string Tag { get; init; }
    [JsonPropertyName("detour")] public string? Detour { get; init; }
}
