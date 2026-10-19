using BoxForge.Builders;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

public sealed class ShadowsocksConverter()
    : ProxyConverterBase("Shadowsocks", "ss", "shadowsocks"), INodeFrontend
{
    NodeConversionResult INodeFrontend.Parse(ClashProxyNode proxy) => ParseForCatalog(proxy);

    protected override SourceFieldSchema Schema => ShadowsocksFrontend.Schema;

    protected override ProxyOutbound ConvertCore(ClashProxyNode node, string name) =>
        SingboxNodeEmitter.Emit(ParseCore(node, name));

    internal override ShadowsocksNode ParseCore(ClashProxyNode node, string name) =>
        ShadowsocksFrontend.ParseValidated(node, name);
}
