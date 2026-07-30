using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public class AnyTlsConverter : IProxyConverter
{
    public bool CanHandle(string proxyType) =>
        string.Equals(proxyType, "anytls", StringComparison.OrdinalIgnoreCase);

    public NodeConversionResult Convert(ClashProxyNode node)
    {
        string name = node.GetString("name") ?? "Unknown-AnyTLS-Node";

        try
        {
            string server = node.GetRequiredString("server");

            return NodeConversionResult.Success(new AnyTlsOutbound
            {
                Tag = name,
                Server = server,
                ServerPort = node.GetRequiredInt("port"),
                Password = node.GetRequiredString("password"),
                IdleTimeout = node.GetString("idle-timeout") ?? node.GetString("idle_timeout"),
                Tls = TlsConfigHelper.Extract(node, server, forceTls: true)
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"AnyTLS 节点 '{name}' 解析失败 -> {ex.Message}");
        }
    }
}
