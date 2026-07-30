using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public class TrojanConverter : IProxyConverter
{
    public bool CanHandle(string proxyType) => proxyType == "trojan";

    public NodeConversionResult Convert(ClashProxyNode node)
    {
        string name = node.GetString("name") ?? "Unknown-Trojan-Node";

        try
        {
            string server = node.GetRequiredString("server");

            return NodeConversionResult.Success(new TrojanOutbound
            {
                Tag = name,
                Server = server,
                ServerPort = node.GetRequiredInt("port"),
                Password = node.GetRequiredString("password"),
                Tls = TlsConfigHelper.Extract(node, server, forceTls: true)
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"Trojan 节点 '{name}' 解析失败 -> {ex.Message}");
        }
    }
}
