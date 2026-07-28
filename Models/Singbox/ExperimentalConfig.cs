using System.Text.Json.Serialization;

namespace SubConvert.Models.Singbox;

public record ExperimentalConfig
{
    [JsonPropertyName("cache_file")] public CacheFileConfig? CacheFile { get; init; }
    [JsonPropertyName("clash_api")] public ClashApiConfig? ClashApi { get; init; }
}

public record CacheFileConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    
    // 缓存 ID，避免切换配置时节点连接复用混乱
    [JsonPropertyName("cache_id")] public string? CacheId { get; init; }
}

public record ClashApiConfig
{
    [JsonPropertyName("external_controller")] public string? ExternalController { get; init; }
    [JsonPropertyName("external_ui")] public string? ExternalUi { get; init; }
    [JsonPropertyName("secret")] public string? Secret { get; init; }
}
