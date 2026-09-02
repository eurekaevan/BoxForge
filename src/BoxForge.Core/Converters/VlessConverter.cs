using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;

namespace BoxForge.Converters;

public sealed class VlessConverter()
    : ProxyConverterBase("VLESS", "vless")
{
    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");

        return new VlessOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = node.GetRequiredInt("port"),
            Uuid = node.GetRequiredString("uuid"),
            Flow = node.GetString("flow"),
            Tls = TlsConfigHelper.Extract(node, server),
            PacketEncoding = "xudp"
        };
    }
}
