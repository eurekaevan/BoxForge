using BoxForge.Models.Clash;
using BoxForge.Helpers;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

internal static class SourceFieldSchemas
{
    public static SourceFieldSchema Common() => new SourceFieldSchema()
        .Mapped("name", "type", "server", "port")
        // Display and subscription annotations do not affect proxy connections.
        .Ignored("metadata", "provider-name", "icon", "description")
        .Conditional("udp", SourceFieldDisposition.IgnoredByDesign,
            (node, _) => node.GetBool("udp")
                ? null
                : "为 false 时会禁用 UDP，而当前出站没有对应限制")
        .Conditional("ip-version", SourceFieldDisposition.Mapped,
            (_, value) => value?.ToString()?.Trim() == "ipv4"
                ? null
                : "当前仅支持 ipv4 节点地址族约束")
        .Conditional("tfo", SourceFieldDisposition.IgnoredByDesign,
            (node, _) => node.GetBool("tfo")
                ? "启用 TCP Fast Open 尚未映射"
                : null)
        .Conditional("mptcp", SourceFieldDisposition.IgnoredByDesign,
            (node, _) => node.GetBool("mptcp")
                ? "启用 MPTCP 尚未映射"
                : null)
        .Conditional("smux", SourceFieldDisposition.IgnoredByDesign,
            (_, value) =>
            {
                try
                {
                    return value is ClashObject options && options.GetNullableBool("enabled") == false
                        ? null : "多路复用配置尚未映射";
                }
                catch (NodeParseException ex)
                {
                    throw ex.AtPath("source.smux");
                }
            }, SourceFieldValueKind.Mapping)
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
            .Alias("sni", "servername")
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
                .Alias("client-fingerprint", "client_fingerprint");
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

    public static SourceFieldSchema NativeTcp() => new SourceFieldSchema()
        .Conditional("network", SourceFieldDisposition.IgnoredByDesign,
            (_, value) => value?.ToString()?.Trim().ToLowerInvariant() is null or "" or "tcp"
                ? null
                : "仅支持原生 TCP；V2Ray Transport 已移除")
        .Unsupported("V2Ray Transport 已移除，不接受其连接选项",
            "ws-opts", "ws_opts", "grpc-opts", "grpc_opts",
            "http-opts", "http_opts", "h2-opts", "h2_opts",
            "http-upgrade-opts", "http_upgrade_opts", "httpupgrade-opts", "httpupgrade_opts",
            "xhttp-opts", "xhttp_opts")
        .Unsupported("多路复用配置尚未映射", "smux-opts", "smux_opts");
}
