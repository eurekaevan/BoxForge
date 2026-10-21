using System.Text;
using BoxForge.Exceptions;
using BoxForge.Helpers;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class VlessProtocol
{
    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: true, supportsUtls: true, forceTls: false))
        .Include(SourceFieldSchemas.NativeTcp())
        .Mapped("uuid", "flow", "packet-encoding")
        .Alias("packet-encoding", "packet_encoding")
        .Conditional("encryption", SourceFieldDisposition.IgnoredByDesign,
            (_, value) => string.IsNullOrWhiteSpace(value?.ToString())
                ? null
                : "非空 VLESS encryption 尚未映射");

    // The parser has already checked the name and schema, preserving
    // the existing diagnostic order without validating the input twice.
    internal static VlessNode ParseValidated(ClashProxyNode node, string name)
    {
        string server = node.GetRequiredString("server");
        TlsSettings? tls = TlsConfigHelper.ExtractSettings(node, server);
        int port = node.GetRequiredInt("port");
        string userId = node.GetRequiredString("uuid");
        VlessFlow flow = ExtractFlow(node);
        string? encoding = node.GetRawString("packet-encoding") ?? node.GetRawString("packet_encoding");

        return new VlessNode(name, server, port, userId,
            flow,
            encoding is "packetaddr" or "packet" ? DatagramEncoding.PacketAddress : DatagramEncoding.Xudp,
            node.GetNullableBool("tls"),
            tls);
    }

    private static VlessFlow ExtractFlow(ClashProxyNode node)
    {
        string flow = node.GetRawString("flow") ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(flow) < 16)
            return VlessFlow.None;
        if (flow.StartsWith("xtls-rprx-vision", StringComparison.Ordinal))
            return VlessFlow.Vision;
        throw new NodeParseException("字段 'flow' 的前 16 字节必须是 xtls-rprx-vision");
    }

}
