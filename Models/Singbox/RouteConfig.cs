using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public record RouteConfig
{
    [JsonPropertyName("rule_set")] public List<SingboxRuleSet> RuleSet { get; init; } = [];
    [JsonPropertyName("rules")] public List<RouteRule> Rules { get; init; } = [];
    [JsonPropertyName("final")] public string? Final { get; init; }
    [JsonPropertyName("auto_detect_interface")] public bool AutoDetectInterface { get; init; } = true;
    [JsonPropertyName("default_http_client")] public string? DefaultHttpClient { get; init; }
}

public record SingboxRuleSet
{
    [JsonPropertyName("type")] public RuleSetType? Type { get; init; }
    [JsonPropertyName("tag")] public string? Tag { get; init; }
    [JsonPropertyName("format")] public RuleSetFormat? Format { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("update_interval")] public string? UpdateInterval { get; init; }
}

public record RouteRule
{
    [JsonPropertyName("type")] public RouteRuleType? Type { get; init; }
    [JsonPropertyName("mode")] public RouteLogicalMode? Mode { get; init; }
    [JsonPropertyName("rules")] public List<RouteRule>? Rules { get; init; }
    [JsonPropertyName("inbound")] public List<string>? Inbound { get; init; }
    [JsonPropertyName("protocol")] public List<string>? Protocol { get; init; }
    [JsonPropertyName("port")] public List<int>? Port { get; init; }
    [JsonPropertyName("network")] public List<string>? Network { get; init; }
    [JsonPropertyName("action")] public RouteRuleAction? Action { get; init; }
    [JsonPropertyName("rule_set")] public List<string>? RuleSet { get; init; }
    [JsonPropertyName("ip_cidr")] public List<string>? IpCidr { get; init; }
    [JsonPropertyName("ip_is_private")] public bool? IpIsPrivate { get; init; }
    [JsonPropertyName("preferred_by")] public List<string>? PreferredBy { get; init; }
    [JsonPropertyName("outbound")] public string? Outbound { get; init; }
    [JsonPropertyName("timeout")] public string? Timeout { get; init; }
}
