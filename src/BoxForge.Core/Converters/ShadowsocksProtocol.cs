using System.Globalization;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

internal static class ShadowsocksProtocol
{
    private static readonly SourceFieldSchema ObfsOptions = new SourceFieldSchema()
        .Mapped("mode", "host");
    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Mapped("cipher", "password")
        .Alias("cipher", "method")
        .Conditional("plugin-opts", SourceFieldDisposition.Mapped,
            ValidatePluginOptions, SourceFieldValueKind.Any)
        .Alias("plugin-opts", "plugin_opts")
        .Conditional("plugin", SourceFieldDisposition.Mapped,
            (_, value) => value is null or "" or "obfs"
                ? null
                : "仅支持 obfs 插件；v2ray-plugin 已移除")
        .Mapped("udp-over-tcp")
        .Conditional("udp-over-tcp-version", SourceFieldDisposition.Mapped,
            (_, value) => value is null || int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) && version is >= 0 and <= 2
                ? null : "UoT 版本必须是整数 0（源默认 v1）、1 或 2")
        .Alias("udp-over-tcp", "udp_over_tcp")
        .Alias("udp-over-tcp-version", "udp_over_tcp_version")
        .Unsupported("当前 Mihomo Shadowsocks 不定义 network，不能生成目标网络限制", "network")
        .Unsupported("插件的 uTLS 指纹尚未映射", "client-fingerprint", "client_fingerprint");

    private static string? ValidatePluginOptions(ClashProxyNode node, object? value)
    {
        string? plugin = node.GetString("plugin");
        if (plugin is null)
        {
            return "指定了插件选项，但没有插件";
        }

        if (plugin != "obfs")
            return "仅支持 obfs 插件；v2ray-plugin 已移除";

        if (value is not ClashObject options)
            return "Mihomo 插件选项必须是对象，不能原样传递 SIP003 字符串";
        ObfsOptions.ValidateNested(options, node, ReferenceEquals(node.GetObject("plugin_opts"), options)
                ? "plugin_opts" : "plugin-opts");
        return null;
    }

    // The parser validates name/schema before interpreting source values.
    internal static ShadowsocksNode ParseValidated(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");
        int port = node.GetRequiredInt("port");
        string method = node.GetString("cipher")
            ?? node.GetString("method")
            ?? throw new NodeParseException("缺失加密方式 (cipher 或 method)");
        string password = node.GetRequiredRawString("password");
        int version = node.GetInt("udp-over-tcp-version")
            ?? node.GetInt("udp_over_tcp_version") ?? 1;
        bool? enabled = node.GetNullableBool("udp-over-tcp")
            ?? node.GetNullableBool("udp_over_tcp");
        string? plugin = node.GetString("plugin");
        ClashObject? options = node.GetObject("plugin-opts") ?? node.GetObject("plugin_opts");
        ShadowsocksObfs? obfs = null;
        if (plugin is not null)
        {
            if (options is null)
                throw new NodeParseException("字段 'plugin-opts.mode' 必须显式指定");
            string? mode = options.GetRawString("mode");
            string host = options.GetRawString("host") ?? "bing.com";
            if (!ShadowsocksObfs.IsValidHost(host))
                throw new NodeParseException("字段 'plugin-opts.host' 必须是非空、无控制字符的主机名");
            if (mode is not ("http" or "tls"))
                throw new NodeParseException("字段 'plugin-opts.mode' 只支持 http 或 tls");
            obfs = new ShadowsocksObfs(
                new(mode == "http" ? ShadowsocksObfsMode.Http : ShadowsocksObfsMode.Tls,
                    true),
                new(host, options.GetPresentField("host") is not null));
        }

        return new ShadowsocksNode(name, server, port,
            new(method, node.GetPresentField("cipher", "method") is not null), password,
            new(enabled, node.GetPresentField("udp-over-tcp", "udp_over_tcp") is not null),
            new(version == 2 ? ShadowsocksUotVersion.V2 : ShadowsocksUotVersion.V1,
                node.GetPresentField("udp-over-tcp-version", "udp_over_tcp_version") is not null),
            new(obfs, node.GetPresentField("plugin") is not null));
    }
}
