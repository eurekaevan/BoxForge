using SubConvert.Models;
using SubConvert.Models.Clash;
using SubConvert.Models.Singbox;
using SubConvert.Helpers;
using SubConvert.Exceptions;

namespace SubConvert.Converters;

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
