using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;

namespace BoxForge.Converters;

public sealed class TrojanConverter()
    : ProxyConverterBase("Trojan", "trojan")
{
    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");

        return new TrojanOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = node.GetRequiredInt("port"),
            Password = node.GetRequiredString("password"),
            Tls = TlsConfigHelper.Extract(node, server, forceTls: true)
        };
    }
}
