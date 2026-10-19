using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;
using System.Text;

namespace BoxForge.Converters;

public sealed class VlessConverter()
    : ProxyConverterBase("VLESS", "vless")
{
    private static readonly SourceFieldSchema SourceFields = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: true, supportsUtls: true, forceTls: false))
        .Include(SourceFieldSchemas.NativeTcp())
        .Mapped("uuid", "flow", "packet-encoding")
        .Alias("packet-encoding", "packet_encoding")
        .Conditional("encryption", SourceFieldDisposition.IgnoredByDesign,
            (_, value) => string.IsNullOrWhiteSpace(value?.ToString())
                ? null
                : "非空 VLESS encryption 尚未映射");

    protected override SourceFieldSchema Schema => SourceFields;

    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");
        OutboundTls? tls = TlsConfigHelper.Extract(node, server);

        return new VlessOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = node.GetRequiredInt("port"),
            Uuid = node.GetRequiredString("uuid"),
            Flow = ExtractFlow(node),
            Tls = tls,
            PacketEncoding = ExtractPacketEncoding(node)
        };
    }

    private static string? ExtractFlow(ClashProxyNode node)
    {
        string flow = node.GetRawString("flow") ?? string.Empty;
        // Mihomo ignores flows shorter than 16 bytes and otherwise uses only
        // the first 16 bytes; the only supported prefix is xtls-rprx-vision.
        if (Encoding.UTF8.GetByteCount(flow) < 16)
            return null;
        if (flow.StartsWith("xtls-rprx-vision", StringComparison.Ordinal))
            return "xtls-rprx-vision";
        throw new NodeParseException("字段 'flow' 的前 16 字节必须是 xtls-rprx-vision");
    }

    private static string ExtractPacketEncoding(ClashProxyNode node) =>
        // With packet-addr absent, Mihomo uses XUDP for every other value,
        // including empty, unknown, and differently cased strings.
        (node.GetRawString("packet-encoding") ?? node.GetRawString("packet_encoding"))
            is "packetaddr" or "packet" ? "packetaddr" : "xudp";
}
