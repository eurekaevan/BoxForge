using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ApiService), typeDiscriminator: "api")]
public abstract record SingboxService
{
    [JsonPropertyName("tag")] public required string Tag { get; init; }
}

public sealed record ApiService : SingboxService
{
    [JsonPropertyName("listen")] public required string Listen { get; init; }
    [JsonPropertyName("listen_port")] public int ListenPort { get; init; }
    [JsonPropertyName("access_control_allow_origin")]
    public List<string> AccessControlAllowOrigin { get; init; } = [];
    [JsonPropertyName("access_control_allow_private_network")]
    public bool AccessControlAllowPrivateNetwork { get; init; }
    [JsonPropertyName("dashboard")] public ApiDashboardConfig? Dashboard { get; init; }
}

public sealed record ApiDashboardConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("http_client")] public required string HttpClient { get; init; }
}
