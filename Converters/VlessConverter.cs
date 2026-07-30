using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public class VlessConverter : IProxyConverter
{
    public bool CanHandle(string proxyType) => proxyType == "vless";

    public NodeConversionResult Convert(ClashProxyNode node)
    {
        string name = node.GetString("name") ?? "Unknown-VLESS-Node";

        try
        {
            string server = node.GetRequiredString("server");

            return NodeConversionResult.Success(new VlessOutbound
            {
                Tag = name,
                Server = server,
                ServerPort = node.GetRequiredInt("port"),
                Uuid = node.GetRequiredString("uuid"),
                Flow = node.GetString("flow"),
                Tls = TlsConfigHelper.Extract(node, server),
                PacketEncoding = "xudp"
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"VLESS 节点 '{name}' 解析失败 -> {ex.Message}");
        }
    }
}
