using BoxForge.Builders;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

public sealed class VlessConverter()
    : ProxyConverterBase("VLESS", "vless"), INodeFrontend
{
    NodeConversionResult INodeFrontend.Parse(ClashProxyNode proxy) => ParseForCatalog(proxy);

    protected override SourceFieldSchema Schema => VlessFrontend.Schema;

    protected override ProxyOutbound ConvertCore(ClashProxyNode node, string name) =>
        SingboxNodeEmitter.Emit(ParseCore(node, name));

    internal override VlessNode ParseCore(ClashProxyNode node, string name) =>
        VlessFrontend.ParseValidated(node, name);
}
