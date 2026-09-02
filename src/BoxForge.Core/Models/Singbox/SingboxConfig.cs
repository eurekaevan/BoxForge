using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

public record SingboxConfig
{
    [JsonPropertyOrder(int.MinValue)]
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://sing-box.sagernet.org/schema.json";

    [JsonPropertyName("log")] public LogConfig Log { get; init; } = new();
    [JsonPropertyName("dns")] public DnsConfig Dns { get; init; } = new();
    [JsonPropertyName("http_clients")] public List<HttpClientConfig> HttpClients { get; init; } = [];
    [JsonPropertyName("inbounds")] public List<Inbound> Inbounds { get; init; } = [];
    [JsonPropertyName("endpoints")] public List<Endpoint>? Endpoints { get; init; }
    [JsonPropertyName("outbounds")] public List<Outbound> Outbounds { get; init; } = [];
    [JsonPropertyName("route")] public RouteConfig Route { get; init; } = new();
    [JsonPropertyName("experimental")] public ExperimentalConfig? Experimental { get; init; }
}
