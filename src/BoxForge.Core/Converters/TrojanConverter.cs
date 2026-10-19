using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;
using BoxForge.Builders;
using BoxForge.Models;

namespace BoxForge.Converters;

public sealed class TrojanConverter()
    : ProxyConverterBase("Trojan", "trojan"), INodeFrontend
{
    NodeConversionResult INodeFrontend.Parse(ClashProxyNode proxy) => ParseForCatalog(proxy);

    protected override SourceFieldSchema Schema => TrojanFrontend.Schema;

    protected override ProxyOutbound ConvertCore(ClashProxyNode node, string name) =>
        SingboxNodeEmitter.Emit(ParseCore(node, name));

    internal override TrojanNode ParseCore(ClashProxyNode node, string name) =>
        TrojanFrontend.ParseValidated(node, name);
}
