using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public record DnsConfig
{
    [JsonPropertyName("timeout")] public string? Timeout { get; init; }
    [JsonPropertyName("servers")] public List<DnsServer> Servers { get; init; } = [];
    [JsonPropertyName("rules")] public List<DnsRule> Rules { get; init; } = [];
    [JsonPropertyName("final")] public string? Final { get; init; }
    [JsonPropertyName("strategy")] public DnsStrategy? Strategy { get; init; }
    [JsonPropertyName("cache_capacity")] public uint? CacheCapacity { get; init; }
    [JsonPropertyName("optimistic")] public DnsOptimisticConfig? Optimistic { get; init; }
    [JsonPropertyName("reverse_mapping")] public bool? ReverseMapping { get; init; }
}

public record DnsOptimisticConfig
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("timeout")] public string? Timeout { get; init; }
}

public record DnsResolverOptions
{
    [JsonPropertyName("server")] public required string Server { get; init; }
    [JsonPropertyName("strategy")] public DnsStrategy? Strategy { get; init; }
    [JsonPropertyName("disable_optimistic_cache")]
    public bool? DisableOptimisticCache { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HttpsDnsServer), typeDiscriminator: "https")]
[JsonDerivedType(typeof(TailscaleDnsServer), typeDiscriminator: "tailscale")]
public abstract record DnsServer
{
    [JsonPropertyOrder(-100)]
    [JsonPropertyName("tag")] public string? Tag { get; init; }
}

public record HttpsDnsServer : DnsServer
{
    [JsonPropertyName("server")] public required string Server { get; init; }
    [JsonPropertyName("detour")] public string? Detour { get; init; }
    [JsonPropertyName("tls")] public required DnsTlsConfig Tls { get; init; }
}

public record DnsTlsConfig
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("server_name")] public required string ServerName { get; init; }
}

public record TailscaleDnsServer : DnsServer
{
    [JsonPropertyName("endpoint")] public required string Endpoint { get; init; }
    [JsonPropertyName("accept_default_resolvers")] public bool? AcceptDefaultResolvers { get; init; }
}

public record DnsRule
{
    [JsonPropertyName("type")] public RouteRuleType? Type { get; init; }
    [JsonPropertyName("mode")] public RouteLogicalMode? Mode { get; init; }
    [JsonPropertyName("rules")] public List<DnsRule>? Rules { get; init; }
    [JsonPropertyName("invert")] public bool? Invert { get; init; }
    [JsonPropertyName("default_interface_address")] public List<string>? DefaultInterfaceAddress { get; init; }
    [JsonPropertyName("timeout")] public string? Timeout { get; init; }
    [JsonPropertyName("rule_set")] public List<string>? RuleSet { get; init; }
    [JsonPropertyName("domain")] public List<string>? Domain { get; init; }
    [JsonPropertyName("domain_suffix")] public List<string>? DomainSuffix { get; init; }
    [JsonPropertyName("query_type")] public List<string>? QueryType { get; init; }
    [JsonPropertyName("preferred_by")] public List<string>? PreferredBy { get; init; }
    [JsonPropertyName("match_response")] public string? MatchResponse { get; init; }
    [JsonPropertyName("ip_cidr")] public List<string>? IpCidr { get; init; }
    [JsonPropertyName("response_rcode")] public DnsResponseCode? ResponseRcode { get; init; }
    [JsonPropertyName("action")] public DnsRuleAction? Action { get; init; }
    [JsonPropertyName("server")] public string? Server { get; init; }
    [JsonPropertyName("tag")] public string? Tag { get; init; }
    [JsonPropertyName("race")] public bool? Race { get; init; }
    [JsonPropertyName("disable_optimistic_cache")] public bool? DisableOptimisticCache { get; init; }
    [JsonPropertyName("rcode")] public DnsResponseCode? Rcode { get; init; }
}
