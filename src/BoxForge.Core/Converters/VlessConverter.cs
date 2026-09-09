using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;

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
            PacketEncoding = ExtractPacketEncoding(node)
        };
    }

    private static string? ExtractPacketEncoding(ClashProxyNode node)
    {
        object? rawValue = node.GetValue("packet-encoding")
            ?? node.GetValue("packet_encoding");
        if (rawValue == null)
        {
            return "xudp";
        }

        string value = rawValue.ToString()?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.Equals("xudp", StringComparison.OrdinalIgnoreCase))
        {
            return "xudp";
        }

        if (value.Equals("packetaddr", StringComparison.OrdinalIgnoreCase))
        {
            return "packetaddr";
        }

        throw new NodeParseException(
            "字段 'packet-encoding' 必须是 xudp、packetaddr 或空字符串");
    }
}
