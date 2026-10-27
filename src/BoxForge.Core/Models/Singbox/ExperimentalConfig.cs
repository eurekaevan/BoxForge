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

    // 由配置名和平台确定的持久缓存身份，节点变更不改变此值。
    [JsonPropertyName("cache_id")] public string? CacheId { get; init; }
}
