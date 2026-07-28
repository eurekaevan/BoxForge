using System.Text.Json.Serialization;

namespace SubConvert.Models.Singbox;

public record DnsConfig
{
    [JsonPropertyName("servers")] public List<DnsServer> Servers { get; init; } = [];
    [JsonPropertyName("rules")] public List<DnsRule> Rules { get; init; } = [];
    [JsonPropertyName("final")] public string Final { get; init; } = "remote";
    [JsonPropertyName("strategy")] public string Strategy { get; init; } = "prefer_ipv4";
    [JsonPropertyName("reverse_mapping")] public bool ReverseMapping { get; init; } = true;
}

public abstract record DnsServer
{
    [JsonPropertyName("tag")] public string? Tag { get; init; }
    [JsonPropertyName("type")] public abstract string Type { get; }
    [JsonPropertyName("server")] public virtual string? Server => null;
    [JsonPropertyName("detour")] public virtual string? Detour => null;
    [JsonPropertyName("endpoint")] public virtual string? Endpoint => null;
    [JsonPropertyName("accept_default_resolvers")] public virtual bool? AcceptDefaultResolvers => null;
}

public record LocalDnsServer : DnsServer
{
    public override string Type => "local";
}

public record HttpsDnsServer : DnsServer
{
    public override string Type => "https";
    public override string Server => ServerAddress;
    public override string? Detour => DetourTag;

    [JsonIgnore] public required string ServerAddress { get; init; }
    [JsonIgnore] public string? DetourTag { get; init; }
}

public record TailscaleDnsServer : DnsServer
{
    public override string Type => "tailscale";
    public override string Endpoint => EndpointTag;
    public override bool? AcceptDefaultResolvers => AcceptDefaultResolversValue;

    [JsonIgnore] public required string EndpointTag { get; init; }
    [JsonIgnore] public bool? AcceptDefaultResolversValue { get; init; }
}

public record DnsRule
{
    [JsonPropertyName("rule_set")] public List<string>? RuleSet { get; init; }
    [JsonPropertyName("domain")] public List<string>? Domain { get; init; }
    [JsonPropertyName("domain_suffix")] public List<string>? DomainSuffix { get; init; }
    [JsonPropertyName("query_type")] public List<string>? QueryType { get; init; }
    [JsonPropertyName("action")] public string? Action { get; init; }
    [JsonPropertyName("server")] public string? Server { get; init; }
    [JsonPropertyName("rcode")] public string? Rcode { get; init; }
    [JsonPropertyName("ip_accept_any")] public bool? IpAcceptAny { get; init; }
}
