using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;

namespace BoxForge.Converters;

public sealed class AnyTlsConverter()
    : ProxyConverterBase("AnyTLS", "anytls")
{
    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");

        return new AnyTlsOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = node.GetRequiredInt("port"),
            Password = node.GetRequiredString("password"),
            IdleTimeout = node.GetString("idle-timeout") ?? node.GetString("idle_timeout"),
            Tls = TlsConfigHelper.Extract(node, server, forceTls: true)
        };
    }
}
