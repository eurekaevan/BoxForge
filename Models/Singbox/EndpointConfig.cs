using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TailscaleEndpoint), typeDiscriminator: "tailscale")]
public abstract record Endpoint
{
    [JsonPropertyOrder(-100)]
    [JsonPropertyName("tag")]
    public required string Tag { get; init; }
}

public record TailscaleEndpoint : Endpoint
{
    [JsonPropertyName("state_directory")] public string? StateDirectory { get; init; }
    [JsonPropertyName("control_url")] public string? ControlUrl { get; init; }
    [JsonPropertyName("hostname")] public string? Hostname { get; init; }
    [JsonPropertyName("accept_routes")] public bool AcceptRoutes { get; init; }
    [JsonPropertyName("exit_node")] public string? ExitNode { get; init; }
    [JsonPropertyName("exit_node_allow_lan_access")] public bool? ExitNodeAllowLanAccess { get; init; }
}
