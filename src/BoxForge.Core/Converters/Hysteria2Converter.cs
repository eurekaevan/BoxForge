using BoxForge.Builders;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

public sealed class Hysteria2Converter()
    : ProxyConverterBase("Hysteria2", "hysteria2"), INodeFrontend
{
    NodeConversionResult INodeFrontend.Parse(ClashProxyNode proxy) => ParseForCatalog(proxy);

    protected override SourceFieldSchema Schema => Hysteria2Frontend.Schema;

    protected override ProxyOutbound ConvertCore(ClashProxyNode node, string name) =>
        SingboxNodeEmitter.Emit(ParseCore(node, name));

    internal override Hysteria2Node ParseCore(ClashProxyNode node, string name) =>
        Hysteria2Frontend.ParseValidated(node, name);
}
