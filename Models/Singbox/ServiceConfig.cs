using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SingboxApiService), typeDiscriminator: "api")]
public abstract record SingboxService
{
    [JsonPropertyName("tag")] public required string Tag { get; init; }
}

public record SingboxApiService : SingboxService
{
    [JsonPropertyName("listen")] public required string Listen { get; init; }
    [JsonPropertyName("listen_port")] public int ListenPort { get; init; }
    [JsonPropertyName("secret")] public required string Secret { get; init; }
    [JsonPropertyName("dashboard")] public required SingboxApiDashboard Dashboard { get; init; }
}

public record SingboxApiDashboard
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("http_client")] public required string HttpClient { get; init; }
}
