using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WebSocketTransport), "ws")]
[JsonDerivedType(typeof(GrpcTransport), "grpc")]
[JsonDerivedType(typeof(HttpUpgradeTransport), "httpupgrade")]
public abstract record V2RayTransport;

public sealed record WebSocketTransport : V2RayTransport
{
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("max_early_data")] public uint? MaxEarlyData { get; init; }
    [JsonPropertyName("early_data_header_name")] public string? EarlyDataHeaderName { get; init; }
}

public sealed record GrpcTransport : V2RayTransport
{
    [JsonPropertyName("service_name")] public required string ServiceName { get; init; }
}

public sealed record HttpUpgradeTransport : V2RayTransport
{
    [JsonPropertyName("host")] public required string Host { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; init; }
}
