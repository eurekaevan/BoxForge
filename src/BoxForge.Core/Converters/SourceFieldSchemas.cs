using BoxForge.Models.Clash;
using BoxForge.Helpers;

namespace BoxForge.Converters;

internal static class SourceFieldSchemas
{
    public static SourceFieldSchema Common() => new SourceFieldSchema()
        .Mapped("name", "type", "server", "port")
        .Ignored("仅用于界面或订阅标注，不参与代理连接", "metadata", "provider-name", "icon", "description")
        .Conditional("udp", SourceFieldDisposition.IgnoredByDesign,
            (node, _) => node.GetBool("udp")
                ? null
                : "为 false 时会禁用 UDP，而当前出站没有对应限制")
        .Conditional("ip-version", SourceFieldDisposition.IgnoredByDesign,
            (_, value) => value?.ToString()?.Trim() == "ipv4"
                ? null
                : "要求的地址族与 BoxForge 当前 IPv4-only 出站策略不等价")
        .Conditional("tfo", SourceFieldDisposition.IgnoredByDesign,
            (node, _) => node.GetBool("tfo")
                ? "启用 TCP Fast Open 尚未映射"
                : null)
        .Conditional("mptcp", SourceFieldDisposition.IgnoredByDesign,
            (node, _) => node.GetBool("mptcp")
                ? "启用 MPTCP 尚未映射"
                : null)
        .Conditional("smux", SourceFieldDisposition.IgnoredByDesign,
            (_, value) => value is ClashObject options
                && options.GetNullableBool("enabled") == false
                    ? null
                    : "多路复用配置尚未映射",
            SourceFieldValueKind.Mapping)
        .Unsupported("指定出站连接方式尚未映射", "interface-name", "routing-mark", "dialer-proxy");

    public static SourceFieldSchema Tls(
        bool supportsReality,
        bool supportsUtls,
        bool forceTls)
    {
        var schema = new SourceFieldSchema()
            .Conditional("tls", SourceFieldDisposition.Mapped,
                (node, _) => forceTls && !node.GetBool("tls")
                    ? "为 false 与此协议要求的 TLS 连接冲突"
                    : null)
            .Conditional("sni", SourceFieldDisposition.Mapped,
                (node, _) => RequireTlsActivation(node, forceTls))
            .ConditionalAlias("sni", "servername",
                (node, _) => RequireTlsActivation(node, forceTls))
            .Conditional("skip-cert-verify", SourceFieldDisposition.Mapped,
                (node, _) => RequireTlsActivation(node, forceTls))
            .Conditional("alpn", SourceFieldDisposition.Mapped,
                (node, value) => RequireTlsActivation(node, forceTls)
                    ?? TlsConfigHelper.ValidateAlpn(value), SourceFieldValueKind.Sequence)
            .Unsupported("源指纹可匹配中间/根证书，但目标 certificate_sha256 仅匹配叶证书，无法保证等价", "fingerprint")
            .Unsupported("独立证书名称校验不能映射为改变实际 SNI 的 server_name", "name-cert-verify")
            .Unsupported("客户端证书尚未映射", "certificate", "private-key")
            .Unsupported("TLS 扩展尚未映射", "ech-opts", "ech_opts",
                "shadow-tls-opts", "shadow_tls_opts", "restls-opts", "restls_opts",
                "jls-opts", "jls_opts", "tlsmirror-opts", "tlsmirror_opts");

        if (supportsUtls)
        {
            schema.Conditional("client-fingerprint", SourceFieldDisposition.Mapped,
                    (node, _) => RequireTlsActivation(node, forceTls))
                .ConditionalAlias("client-fingerprint", "client_fingerprint",
                    (node, _) => RequireTlsActivation(node, forceTls));
        }
        else
        {
            schema.Unsupported("此协议的 QUIC TLS 不支持 uTLS 指纹",
                "client-fingerprint", "client_fingerprint");
        }

        if (supportsReality)
        {
            var reality = new SourceFieldSchema()
                .Mapped("public-key", "short-id")
                .Alias("public-key", "public_key")
                .Alias("short-id", "short_id")
                .Unsupported("Reality 混合密钥交换选项尚未映射",
                    "support-x25519mlkem768", "support_x25519mlkem768");
            schema.Nested("reality-opts", reality)
                .Alias("reality-opts", "reality_opts");
        }
        else
        {
            schema.Unsupported("此协议的 Reality 选项尚未映射",
                "reality-opts", "reality_opts");
        }

        return schema;
    }

    private static string? RequireTlsActivation(
        ClashProxyNode node,
        bool forceTls) =>
        forceTls || node.GetBool("tls")
            || node.GetObject("reality-opts") is not null
            || node.GetObject("reality_opts") is not null
                ? null
                : "需要启用 TLS 或 Reality，否则该值不会参与连接";

    public static SourceFieldSchema V2RayTransport(bool supportsGrpc)
    {
        var ws = new SourceFieldSchema()
            .Mapped("path", "max-early-data", "early-data-header-name")
            .Alias("max-early-data", "max_early_data")
            .Alias("early-data-header-name", "early_data_header_name")
            .Conditional("headers", SourceFieldDisposition.Mapped,
                (_, value) => TransportConfigHelper.ValidateHeaders(value),
                SourceFieldValueKind.Mapping)
            .Mapped("v2ray-http-upgrade")
            .Alias("v2ray-http-upgrade", "v2ray_http_upgrade")
            .Conditional("v2ray-http-upgrade-fast-open", SourceFieldDisposition.IgnoredByDesign,
                (_, value) => bool.TryParse(value?.ToString(), out bool enabled)
                    ? enabled ? "fast-open 没有等价的 sing-box HTTPUpgrade 字段" : null
                    : "必须是 true 或 false")
            .Alias("v2ray-http-upgrade-fast-open", "v2ray_http_upgrade_fast_open");
        var grpc = new SourceFieldSchema()
            .Mapped("grpc-service-name")
            .Alias("grpc-service-name", "grpc_service_name")
            .Unsupported("高级 gRPC 连接池/探测语义尚未映射",
                "grpc-user-agent", "grpc_user_agent", "ping-interval", "ping_interval",
                "max-connections", "max_connections", "min-streams", "min_streams",
                "max-streams", "max_streams");

        var schema = new SourceFieldSchema()
            .Conditional("network", SourceFieldDisposition.Mapped,
                (_, value) => value?.ToString()?.Trim().ToLowerInvariant() is null or "" or "tcp"
                    || value is "ws" || (supportsGrpc && value is "grpc") ? null
                    : value is "grpc"
                        ? "Trojan gRPC 的 HTTP authority 与目标 grpc-lite 不等价，暂不支持"
                        : "此 transport 没有经过验证的精确映射")
            .Nested("ws-opts", ws, (node, _) => RequireNetwork(node, "ws"))
            .Alias("ws-opts", "ws_opts")
            .Unsupported("transport 选项尚未映射",
            "http-opts", "http_opts", "h2-opts", "h2_opts",
            "http-upgrade-opts", "http_upgrade_opts",
                "xhttp-opts", "xhttp_opts", "smux-opts", "smux_opts");
        return supportsGrpc
            ? schema.Nested("grpc-opts", grpc, (node, _) => RequireNetwork(node, "grpc"))
                .Alias("grpc-opts", "grpc_opts")
            : schema.Unsupported("Trojan gRPC 的 HTTP authority 无法精确保留", "grpc-opts", "grpc_opts");
    }

    private static string? RequireNetwork(ClashProxyNode node, string network) =>
        node.GetString("network") == network ? null : $"需要 network={network}，否则选项不会参与连接";
}
