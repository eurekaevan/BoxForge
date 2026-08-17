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
    [JsonPropertyName("domain_resolver")] public required string DomainResolver { get; init; }
    [JsonPropertyName("state_directory")] public required string StateDirectory { get; init; }
    [JsonPropertyName("accept_routes")] public bool AcceptRoutes { get; init; }
    [JsonPropertyName("taildrop_directory")] public required string TaildropDirectory { get; init; }
}
