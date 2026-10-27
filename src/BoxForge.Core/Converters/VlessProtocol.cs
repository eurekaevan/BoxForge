using System.Text;
using BoxForge.Helpers;
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

    internal static VlessNode Parse(NodeFieldReader node, string name)
    {
        string server = node.RequiredString("server");
        TlsSettings? tls = TlsConfigHelper.ExtractSettings(node, server);
        int port = node.RequiredPort("port");
        string userId = node.RequiredString("uuid");
        VlessFlow flow = ExtractFlow(node);
        string? encoding = node.RawString("packet-encoding");

        return new VlessNode(name, server, port, userId,
            flow,
            encoding is "packetaddr" or "packet" ? DatagramEncoding.PacketAddress : DatagramEncoding.Xudp,
            node.OptionalBool("tls"),
            tls);
    }

    private static VlessFlow ExtractFlow(NodeFieldReader node)
    {
        string flow = node.RawString("flow") ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(flow) < 16)
            return VlessFlow.None;
        if (flow.StartsWith("xtls-rprx-vision", StringComparison.Ordinal))
            return VlessFlow.Vision;
        throw node.Error("flow", "字段 'flow' 的前 16 字节必须是 xtls-rprx-vision");
    }

}
