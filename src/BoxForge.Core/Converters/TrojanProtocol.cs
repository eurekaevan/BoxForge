using BoxForge.Helpers;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class TrojanProtocol
{
    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: true, supportsUtls: true, forceTls: true))
        .Include(SourceFieldSchemas.NativeTcp())
        .Mapped("password")
        .Unsupported("Trojan Shadowsocks 加密选项尚未映射", "ss-opts", "ss_opts");

    // The parser validates the name and schema before this call.
    internal static TrojanNode ParseValidated(ClashProxyNode node, string name)
    {
        string server = node.GetRequiredString("server");
        var tls = TlsConfigHelper.ExtractSettings(node, server, forceTls: true)
            ?? throw new InvalidOperationException("Trojan TLS interpretation unexpectedly returned no settings.");
        return new TrojanNode(name, server, node.GetRequiredPort("port"),
            node.GetRequiredRawString("password"),
            new(node.GetNullableBool("tls"), node.GetPresentField("tls") is not null),
            tls);
    }
}
