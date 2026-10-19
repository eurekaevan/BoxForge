using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Exceptions;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

public interface IProxyConverter
{
    bool CanHandle(string proxyType);

    NodeConversionResult Convert(ClashProxyNode proxy);
}

internal interface INodeFrontend
{
    NodeConversionResult Parse(ClashProxyNode proxy);
}

public abstract class ProxyConverterBase(
    string displayName,
    params string[] supportedTypes) : IProxyConverter
{
    private readonly HashSet<string> types = new(
        supportedTypes,
        StringComparer.OrdinalIgnoreCase);

    protected abstract SourceFieldSchema Schema { get; }

    public bool CanHandle(string proxyType) => types.Contains(proxyType.Trim());

    public NodeConversionResult Convert(ClashProxyNode proxy) => Convert(proxy, parseOnly: false);

    internal NodeConversionResult ParseForCatalog(ClashProxyNode proxy) => Convert(proxy, parseOnly: true);

    private NodeConversionResult Convert(ClashProxyNode proxy, bool parseOnly)
    {
        string displayNodeName = proxy.GetString("name") ?? "<未命名>";
        try
        {
            string name = proxy.GetRequiredString("name");
            Schema.Validate(proxy);
            if (parseOnly && ParseCore(proxy, name) is { } node)
            {
                node.Validate();
                return new ParsedNode(node);
            }
            return NodeConversionResult.Success(ConvertCore(proxy, name));
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail(
                $"{displayName} 节点 '{displayNodeName}' 解析失败 -> {ex.Message}");
        }
    }

    // Existing external converters can continue implementing only ConvertCore.
    internal virtual ProxyNode? ParseCore(ClashProxyNode node, string name) => null;

    protected abstract ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name);
}
