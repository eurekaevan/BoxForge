using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public sealed class ShadowsocksConverter()
    : ProxyConverterBase("Shadowsocks", "ss", "shadowsocks")
{
    private static readonly SourceFieldSchema SourceFields = SourceFieldSchemas.Common()
        .Mapped("cipher", "password")
        .Alias("cipher", "method")
        .Conditional("plugin-opts", SourceFieldDisposition.Mapped,
            ValidatePluginOptions, SourceFieldValueKind.Any)
        .ConditionalAlias("plugin-opts", "plugin_opts", ValidatePluginOptions)
        .Conditional("plugin", SourceFieldDisposition.Mapped,
            (_, value) => value?.ToString() is "v2ray-plugin" or "obfs-local"
                ? null
                : "此插件名称不能直接映射到 sing-box 支持的 SIP003 插件")
        .Conditional("udp-over-tcp", SourceFieldDisposition.Mapped,
            (node, _) => node.GetBool("udp-over-tcp")
                ? "Mihomo 默认 UoT v1 与 sing-box 默认 v2 不同，版本映射尚未实现"
                : null)
        .ConditionalAlias("udp-over-tcp", "udp_over_tcp",
            (node, _) => node.GetBool("udp_over_tcp")
                ? "Mihomo 默认 UoT v1 与 sing-box 默认 v2 不同，版本映射尚未实现"
                : null)
        .Unsupported("UoT 版本尚未映射", "udp-over-tcp-version", "udp_over_tcp_version")
        .Unsupported("Shadowsocks 启用网络类型尚未映射", "network")
        .Unsupported("插件的 uTLS 指纹尚未映射", "client-fingerprint", "client_fingerprint");

    protected override SourceFieldSchema Schema => SourceFields;

    private static string? ValidatePluginOptions(ClashProxyNode node, object? value)
    {
        if (node.GetString("plugin") is null)
        {
            return "指定了插件选项，但没有插件";
        }

        return value is string
            ? null
            : "非字符串形式的 Mihomo 插件选项尚未能保证与 sing-box 选项等价";
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

        return new ShadowsocksOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = port,
            Method = method,
            Password = password,
            Plugin = node.GetString("plugin"),
            PluginOpts = node.GetRawString("plugin-opts")
                ?? node.GetRawString("plugin_opts"),
            UdpOverTcp = node.GetNullableBool("udp-over-tcp")
                ?? node.GetNullableBool("udp_over_tcp")
        };
    }
}
