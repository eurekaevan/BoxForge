using BoxForge.Builders;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

public sealed class AnyTlsConverter()
    : ProxyConverterBase("AnyTLS", "anytls"), INodeFrontend
{
    NodeConversionResult INodeFrontend.Parse(ClashProxyNode proxy) => ParseForCatalog(proxy);

    protected override SourceFieldSchema Schema => AnyTlsFrontend.Schema;

    protected override ProxyOutbound ConvertCore(ClashProxyNode node, string name) =>
        SingboxNodeEmitter.Emit(ParseCore(node, name));

    internal override AnyTlsNode ParseCore(ClashProxyNode node, string name) =>
        AnyTlsFrontend.ParseValidated(node, name);
}
