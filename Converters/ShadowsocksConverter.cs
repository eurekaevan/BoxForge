using SubConvert.Models;
using SubConvert.Models.Clash;
using SubConvert.Models.Singbox;
using SubConvert.Exceptions;

namespace SubConvert.Converters;

public class ShadowsocksConverter : IProxyConverter
{
    public bool CanHandle(string proxyType) => 
        string.Equals(proxyType, "ss", StringComparison.OrdinalIgnoreCase) || 
        string.Equals(proxyType, "shadowsocks", StringComparison.OrdinalIgnoreCase);

    public NodeConversionResult Convert(ClashProxyNode node)
    {
        string name = node.GetString("name") ?? "Unknown-SS-Node";

        try
        {
            string server = node.GetRequiredString("server");
            int port = node.GetRequiredInt("port");
            string method = node.GetString("cipher") ?? node.GetString("method") ?? throw new NodeParseException("缺失加密方式 (cipher 或 method)");
            string password = node.GetRequiredString("password");

            string? plugin = node.GetString("plugin");
            string? pluginOpts = ExtractPluginOptions(node);
            bool? udpOverTcp = node.GetNullableBool("udp-over-tcp") ?? node.GetNullableBool("udp_over_tcp");

            return NodeConversionResult.Success(new ShadowsocksOutbound
            {
                Tag = name,
                Server = server,
                ServerPort = port,
                Method = method,
                Password = password,
                Plugin = plugin,
                PluginOpts = pluginOpts,
                UdpOverTcp = udpOverTcp
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"Shadowsocks 节点 '{name}' 解析失败 -> {ex.Message}");
        }
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
                    .Select(property => $"{property.Key}={property.Value}"));
        }

        return value?.ToString();
    }
}
