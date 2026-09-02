using System.Globalization;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public sealed class ShadowsocksConverter()
    : ProxyConverterBase("Shadowsocks", "ss", "shadowsocks")
{
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
            PluginOpts = ExtractPluginOptions(node),
            UdpOverTcp = node.GetNullableBool("udp-over-tcp")
                ?? node.GetNullableBool("udp_over_tcp")
        };
    }

    private static string? ExtractPluginOptions(ClashProxyNode node)
    {
        var value = node.GetValue("plugin-opts") ?? node.GetValue("plugin_opts");
        if (value is string stringOptions)
        {
            return stringOptions;
        }

        if (value is ClashObject objectOptions)
        {
            return string.Join(
                ";",
                objectOptions.Properties
                    .Where(property => property.Value != null)
                    .OrderBy(property => property.Key, StringComparer.Ordinal)
                    .Select(property =>
                        $"{property.Key}={FormatPluginOptionValue(property.Key, property.Value!)}"));
        }

        return value == null
            ? null
            : FormatPluginOptionValue("plugin-opts", value);
    }

    private static string FormatPluginOptionValue(string key, object value)
    {
        if (value is ClashObject
            || value is System.Collections.IEnumerable and not string)
        {
            throw new NodeParseException(
                $"插件选项 '{key}' 必须是标量值");
        }

        return System.Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? throw new NodeParseException($"插件选项 '{key}' 不能为空");
    }
}
