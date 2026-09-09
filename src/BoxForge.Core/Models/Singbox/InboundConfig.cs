using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public record Inbound
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("tag")] public required string Tag { get; init; }
    [JsonPropertyName("listen")] public string? Listen { get; init; }
    [JsonPropertyName("listen_port")] public int? ListenPort { get; init; }
    [JsonPropertyName("address")] public List<string>? Address { get; init; }
    [JsonPropertyName("dns_mode")] public string? DnsMode { get; init; }
    [JsonPropertyName("auto_route")] public bool? AutoRoute { get; init; }
    [JsonPropertyName("strict_route")] public bool? StrictRoute { get; init; }
    [JsonPropertyName("stack")] public string? Stack { get; init; }
    [JsonPropertyName("mtu")] public int? Mtu { get; init; }
    [JsonPropertyName("auto_redirect")] public bool? AutoRedirect { get; init; }
}
