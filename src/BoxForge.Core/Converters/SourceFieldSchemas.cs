using BoxForge.Models.Clash;

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
            .Unsupported("TLS ALPN 尚未映射", "alpn")
            .Unsupported("证书指纹校验尚未映射", "fingerprint", "name-cert-verify")
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

    public static SourceFieldSchema TcpTransport() => new SourceFieldSchema()
        .Conditional("network", SourceFieldDisposition.IgnoredByDesign,
            (_, value) => value?.ToString()?.Trim().ToLowerInvariant() is null or "" or "tcp"
                ? null
                : "非 TCP transport 尚未映射")
        .Unsupported("transport 选项尚未映射",
            "ws-opts", "ws_opts", "grpc-opts", "grpc_opts",
            "http-opts", "http_opts", "h2-opts", "h2_opts",
            "http-upgrade-opts", "http_upgrade_opts",
            "xhttp-opts", "xhttp_opts", "smux-opts", "smux_opts");
}
