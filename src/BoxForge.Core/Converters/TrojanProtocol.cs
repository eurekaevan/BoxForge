using BoxForge.Helpers;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class TrojanProtocol
{
    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: true, supportsUtls: true, forceTls: true))
        .Include(SourceFieldSchemas.NativeTcp())
        .Mapped("password")
        .Unsupported("Trojan Shadowsocks 加密选项尚未映射", "ss-opts", "ss_opts");

    internal static TrojanNode Parse(NodeFieldReader node, string name)
    {
        string server = node.RequiredString("server");
        var tls = TlsConfigHelper.ExtractSettings(node, server, forceTls: true)
            ?? throw new InvalidOperationException("Trojan TLS interpretation unexpectedly returned no settings.");
        return new TrojanNode(name, server, node.RequiredPort("port"),
            node.RequiredRawString("password"),
            new(node.OptionalBool("tls"), node.PresentField("tls") is not null),
            tls);
    }
}
