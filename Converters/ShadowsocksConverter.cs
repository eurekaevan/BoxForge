using System.Collections;
using SubConvert.Models;
using SubConvert.Models.Singbox;
using SubConvert.Extensions;
using SubConvert.Exceptions;

namespace SubConvert.Converters;

public class ShadowsocksConverter : IProxyConverter
{
    public bool CanHandle(string proxyType) => 
        string.Equals(proxyType, "ss", StringComparison.OrdinalIgnoreCase) || 
        string.Equals(proxyType, "shadowsocks", StringComparison.OrdinalIgnoreCase);

    public NodeConversionResult Convert(Dictionary<string, object> p)
    {
        string name = p.GetString("name") ?? "Unknown-SS-Node";

        try
        {
            string server = p.GetRequiredString("server");
            int port = p.GetRequiredInt("port");
            string method = p.GetString("cipher") ?? p.GetString("method") ?? throw new NodeParseException("缺失加密方式 (cipher 或 method)");
            string password = p.GetRequiredString("password");

            string? plugin = p.GetString("plugin");
            string? pluginOpts = ExtractPluginOpts(p);
            bool? udpOverTcp = p.GetNullableBool("udp-over-tcp") ?? p.GetNullableBool("udp_over_tcp");

            return NodeConversionResult.Success(new ShadowsocksOutbound
            {
                Tag = name,
                Server = server,
                ServerPort = port,
                Method = method,
                Password = password,
                Plugin = plugin,
                PluginOpts = pluginOpts,
                UdpOverTcp = udpOverTcp,
                DomainResolver = "node-resolver",
                ConnectTimeout = "5s"
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"Shadowsocks 节点 '{name}' 解析失败 -> {ex.Message}");
        }
    }

    private static string? ExtractPluginOpts(Dictionary<string, object> dict)
    {
        if (!dict.TryGetValue("plugin-opts", out var val) && !dict.TryGetValue("plugin_opts", out var val2))
        {
            val = null;
        }
        else if (val == null)
        {
            val = dict.GetValueOrDefault("plugin_opts");
        }

        if (val == null) return null;

        if (val is string strOpts) return strOpts;

        if (val is IDictionary dictOpts)
        {
            var pairs = new List<string>();
            foreach (DictionaryEntry entry in dictOpts)
            {
                if (entry.Key != null && entry.Value != null)
                {
                    pairs.Add($"{entry.Key}={entry.Value}");
                }
            }
            return string.Join(";", pairs);
        }

        return val.ToString();
    }
}
