using System.Text.Json.Serialization;

namespace SubConvert.Models.Singbox;

public record SingboxConfig
{
    [JsonPropertyName("log")] public LogConfig Log { get; init; } = new();
    [JsonPropertyName("dns")] public DnsConfig Dns { get; init; } = new();
    [JsonPropertyName("inbounds")] public List<Inbound> Inbounds { get; init; } = [];
    [JsonPropertyName("endpoints")] public List<Endpoint>? Endpoints { get; init; }
    [JsonPropertyName("outbounds")] public List<Outbound> Outbounds { get; init; } = [];
    [JsonPropertyName("route")] public RouteConfig Route { get; init; } = new();
    [JsonPropertyName("experimental")] public ExperimentalConfig? Experimental { get; init; }
}
