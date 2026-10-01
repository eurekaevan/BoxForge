using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public sealed class ShadowsocksConverter()
    : ProxyConverterBase("Shadowsocks", "ss", "shadowsocks")
{
    private static readonly SourceFieldSchema ObfsOptions = new SourceFieldSchema()
        .Mapped("mode", "host");
    private static readonly SourceFieldSchema V2RayOptions = new SourceFieldSchema()
        .Mapped("mode", "host", "path", "mux", "tls")
        .Unsupported("目标内置插件无法精确表达此选项", "headers", "skip-cert-verify",
            "fingerprint", "certificate", "private-key", "name-cert-verify", "ech-opts",
            "v2ray-http-upgrade", "v2ray-http-upgrade-fast-open");
    private static readonly SourceFieldSchema SourceFields = SourceFieldSchemas.Common()
        .Mapped("cipher", "password")
        .Alias("cipher", "method")
        .Conditional("plugin-opts", SourceFieldDisposition.Mapped,
            ValidatePluginOptions, SourceFieldValueKind.Any)
        .ConditionalAlias("plugin-opts", "plugin_opts", ValidatePluginOptions)
        .Conditional("plugin", SourceFieldDisposition.Mapped,
            (_, value) => value is null or "" or "v2ray-plugin" or "obfs"
                ? null
                : "此插件名称不能直接映射到 sing-box 支持的 SIP003 插件")
        .Mapped("udp-over-tcp")
        .Conditional("udp-over-tcp-version", SourceFieldDisposition.Mapped,
            (_, value) => value is null || int.TryParse(value.ToString(), out int version) && version is >= 0 and <= 2
                ? null : "UoT 版本必须是整数 0（源默认 v1）、1 或 2")
        .Alias("udp-over-tcp", "udp_over_tcp")
        .Alias("udp-over-tcp-version", "udp_over_tcp_version")
        .Unsupported("当前 Mihomo Shadowsocks 不定义 network，不能生成目标网络限制", "network")
        .Unsupported("插件的 uTLS 指纹尚未映射", "client-fingerprint", "client_fingerprint");

    protected override SourceFieldSchema Schema => SourceFields;

    private static string? ValidatePluginOptions(ClashProxyNode node, object? value)
    {
        if (node.GetString("plugin") is null)
        {
            return "指定了插件选项，但没有插件";
        }

        if (value is not ClashObject options)
            return "Mihomo 插件选项必须是对象，不能原样传递 SIP003 字符串";
        (node.GetString("plugin") == "obfs" ? ObfsOptions : V2RayOptions)
            .ValidateNested(options, node, ReferenceEquals(node.GetObject("plugin_opts"), options)
                ? "plugin_opts" : "plugin-opts");
        return null;
    }

    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");
        int port = node.GetRequiredInt("port");
        string method = node.GetString("cipher")
            ?? node.GetString("method")
            ?? throw new NodeParseException("缺失加密方式 (cipher 或 method)");
        string password = node.GetRequiredString("password");
        int version = node.GetInt("udp-over-tcp-version")
            ?? node.GetInt("udp_over_tcp_version") ?? 1;
        bool enabled = node.GetNullableBool("udp-over-tcp")
            ?? node.GetNullableBool("udp_over_tcp") ?? false;
        string? plugin = node.GetString("plugin");
        ClashObject? options = node.GetObject("plugin-opts") ?? node.GetObject("plugin_opts");
        string? pluginOptions = null;
        if (plugin is not null)
        {
            if (options is null)
                throw new NodeParseException("字段 'plugin-opts.mode' 必须显式指定");
            string? mode = options.GetRawString("mode");
            string host = options.GetRawString("host") ?? "bing.com";
            if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsControl))
                throw new NodeParseException("字段 'plugin-opts.host' 必须是非空、无控制字符的主机名");
            if (plugin == "obfs")
            {
                if (mode is not ("http" or "tls"))
                    throw new NodeParseException("字段 'plugin-opts.mode' 只支持 http 或 tls");
                plugin = "obfs-local";
                pluginOptions = $"obfs={Escape(mode)};obfs-host={Escape(host)}";
            }
            else
            {
                if (mode != "websocket")
                    throw new NodeParseException("字段 'plugin-opts.mode' 只支持 websocket");
                if (options.GetNullableBool("mux") != false)
                    throw new NodeParseException("字段 'plugin-opts.mux' 必须显式关闭；默认复用的精确映射尚未验证");
                string path = options.GetRawString("path") ?? "/";
                if (path.Length == 0) path = "/";
                if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
                    || path.Any(char.IsControl) || path.IndexOfAny(['?', '#', '%']) >= 0)
                    throw new NodeParseException("字段 'plugin-opts.path' 只支持普通绝对路径");
                pluginOptions = $"mode=websocket;host={Escape(host)};path={Escape(path)};mux=0";
                if (options.GetBool("tls")) pluginOptions += ";tls";
            }
        }

        return new ShadowsocksOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = port,
            Method = method,
            Password = password,
            Plugin = plugin,
            PluginOpts = pluginOptions,
            UdpOverTcp = enabled ? new UdpOverTcpOptions
            {
                Enabled = true,
                Version = version == 0 ? 1 : version
            } : null
        };
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\")
        .Replace(";", "\\;").Replace("=", "\\=");
}
