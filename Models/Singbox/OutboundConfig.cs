using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SelectorOutbound), typeDiscriminator: "selector")]
[JsonDerivedType(typeof(DirectOutbound), typeDiscriminator: "direct")]
[JsonDerivedType(typeof(VlessOutbound), typeDiscriminator: "vless")]
[JsonDerivedType(typeof(TrojanOutbound), typeDiscriminator: "trojan")]
[JsonDerivedType(typeof(Hysteria2Outbound), typeDiscriminator: "hysteria2")]
[JsonDerivedType(typeof(ShadowsocksOutbound), typeDiscriminator: "shadowsocks")]
[JsonDerivedType(typeof(AnyTlsOutbound), typeDiscriminator: "anytls")]
public abstract record Outbound
{
    [JsonPropertyName("tag")] public required string Tag { get; init; }
}

public record SelectorOutbound : Outbound
{
    [JsonPropertyName("outbounds")] public required List<string> Outbounds { get; init; }
    [JsonPropertyName("default")] public string? Default { get; init; }
    [JsonPropertyName("interrupt_exist_connections")] public bool? InterruptExistConnections { get; init; }
}

public record DirectOutbound : Outbound
{
    [JsonPropertyName("domain_resolver")] public required string DomainResolver { get; init; }
}

public abstract record ProxyOutbound : Outbound
{
    [JsonPropertyName("server")] public required string Server { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("server_port")] public int? ServerPort { get; init; }
    [JsonPropertyName("domain_resolver")] public string DomainResolver { get; init; } = "node-resolver";
    [JsonPropertyName("connect_timeout")] public string ConnectTimeout { get; init; } = "5s";
}

public record VlessOutbound : ProxyOutbound
{
    [JsonPropertyName("uuid")] public required string Uuid { get; init; }
    [JsonPropertyName("flow")] public string? Flow { get; init; }
    [JsonPropertyName("packet_encoding")] public string? PacketEncoding { get; init; }
    [JsonPropertyName("tls")] public OutboundTls? Tls { get; init; }
}

public record TrojanOutbound : ProxyOutbound
{
    [JsonPropertyName("password")] public required string Password { get; init; }
    [JsonPropertyName("tls")] public OutboundTls? Tls { get; init; }
}

public record Hysteria2Outbound : ProxyOutbound
{
    [JsonPropertyName("server_ports")] public List<string>? ServerPorts { get; init; }
    [JsonPropertyName("hop_interval")] public string HopInterval { get; init; } = "30s";
    [JsonPropertyName("hop_interval_max")] public string HopIntervalMax { get; init; } = "60s";
    [JsonPropertyName("bbr_profile")] public string BbrProfile { get; init; } = "standard";
    [JsonPropertyName("password")] public required string Password { get; init; }
    [JsonPropertyName("obfs")] public OutboundObfs? Obfs { get; init; }
    [JsonPropertyName("tls")] public OutboundTls? Tls { get; init; }
}

public record ShadowsocksOutbound : ProxyOutbound
{
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("password")] public required string Password { get; init; }
    [JsonPropertyName("plugin")] public string? Plugin { get; init; }
    [JsonPropertyName("plugin_opts")] public string? PluginOpts { get; init; }
    [JsonPropertyName("udp_over_tcp")] public bool? UdpOverTcp { get; init; }
}

public record AnyTlsOutbound : ProxyOutbound
{
    [JsonPropertyName("password")] public required string Password { get; init; }
    [JsonPropertyName("idle_timeout")] public string? IdleTimeout { get; init; }
    [JsonPropertyName("tls")] public OutboundTls? Tls { get; init; }
}

public record Utls
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("fingerprint")] public required string Fingerprint { get; init; }
}

public record OutboundTls
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("server_name")] public required string ServerName { get; init; }
    [JsonPropertyName("insecure")] public bool? Insecure { get; init; }
    [JsonPropertyName("utls")] public Utls? Utls { get; init; }
    [JsonPropertyName("alpn")] public List<string>? Alpn { get; init; }
    [JsonPropertyName("reality")] public OutboundReality? Reality { get; init; }
}

public record OutboundReality
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("public_key")] public required string PublicKey { get; init; }
    [JsonPropertyName("short_id")] public required string ShortId { get; init; }
}

public record OutboundObfs
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("password")] public string? Password { get; init; }
}
