using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public record ExperimentalConfig
{
    [JsonPropertyName("cache_file")] public CacheFileConfig? CacheFile { get; init; }
}

public record CacheFileConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("store_dns")] public bool StoreDns { get; init; }

    // 缓存 ID，避免切换配置时节点连接复用混乱
    [JsonPropertyName("cache_id")] public string? CacheId { get; init; }
}
