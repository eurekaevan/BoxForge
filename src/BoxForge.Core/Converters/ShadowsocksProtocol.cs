using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class ShadowsocksProtocol
{
    private static readonly SourceFieldSchema ObfsOptions = new SourceFieldSchema()
        .Mapped("mode", "host");
    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Mapped("cipher", "password")
        .Alias("cipher", "method")
        .Nested("plugin-opts", ObfsOptions, ValidatePluginOptions)
        .Alias("plugin-opts", "plugin_opts")
        .Conditional("plugin", SourceFieldDisposition.Mapped,
            (_, value) => value is null or "" or "obfs"
                ? null
                : "仅支持 obfs 插件；v2ray-plugin 已移除")
        .Mapped("udp-over-tcp")
        .Conditional("udp-over-tcp-version", SourceFieldDisposition.Mapped,
            (node, _) =>
            {
                node.OptionalInt("udp-over-tcp-version", 0, 2);
                return null;
            })
        .Alias("udp-over-tcp", "udp_over_tcp")
        .Alias("udp-over-tcp-version", "udp_over_tcp_version")
        .Unsupported("当前 Mihomo Shadowsocks 不定义 network，不能生成目标网络限制", "network")
        .Unsupported("插件的 uTLS 指纹尚未映射", "client-fingerprint", "client_fingerprint");

    private static string? ValidatePluginOptions(NodeFieldReader node, object? value)
    {
        string? plugin = node.String("plugin");
        if (plugin is null)
        {
            return "指定了插件选项，但没有插件";
        }

        if (plugin != "obfs")
            return "仅支持 obfs 插件；v2ray-plugin 已移除";

        if (value is not ClashObject)
            return "Mihomo 插件选项必须是对象，不能原样传递 SIP003 字符串";
        return null;
    }

    internal static ShadowsocksNode Parse(
        NodeFieldReader node,
        string name)
    {
        string server = node.RequiredString("server");
        int port = node.RequiredPort("port");
        string method = node.String("cipher")
            ?? throw node.Error(node.PresentField("cipher") ?? "cipher", "缺失加密方式 (cipher 或 method)");
        string password = node.RequiredRawString("password");
        int version = node.OptionalInt("udp-over-tcp-version") ?? 1;
        bool? enabled = node.OptionalBool("udp-over-tcp");
        string? plugin = node.String("plugin");
        NodeFieldReader? options = node.OptionalObject("plugin-opts");
        ShadowsocksObfs? obfs = null;
        if (plugin is not null)
        {
            string optionsField = node.PresentField("plugin-opts") ?? "plugin-opts";
            if (options is null)
                throw node.Error(optionsField + ".mode", "字段 'plugin-opts.mode' 必须显式指定");
            string? mode = options.RawString("mode");
            string host = options.RawString("host") ?? "bing.com";
            if (!ShadowsocksObfs.IsValidHost(host))
                throw options.Error("host", "字段 'plugin-opts.host' 必须是非空、无控制字符的主机名");
            if (mode is not ("http" or "tls"))
                throw options.Error("mode", "字段 'plugin-opts.mode' 只支持 http 或 tls");
            obfs = new ShadowsocksObfs(
                mode == "http" ? ShadowsocksObfsMode.Http : ShadowsocksObfsMode.Tls,
                host);
        }

        return new ShadowsocksNode(name, server, port,
            method, password, enabled,
            version == 2 ? ShadowsocksUotVersion.V2 : ShadowsocksUotVersion.V1, obfs);
    }
}
