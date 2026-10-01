using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public interface IProxyConverter
{
    SourceFieldSchema SourceSchema { get; }

    bool CanHandle(string proxyType);

    NodeConversionResult Convert(ClashProxyNode proxy);
}

public abstract class ProxyConverterBase(
    string displayName,
    params string[] supportedTypes) : IProxyConverter
{
    private readonly HashSet<string> types = new(
        supportedTypes,
        StringComparer.OrdinalIgnoreCase);

    public SourceFieldSchema SourceSchema => Schema;

    protected abstract SourceFieldSchema Schema { get; }

    public bool CanHandle(string proxyType) => types.Contains(proxyType.Trim());

    public NodeConversionResult Convert(ClashProxyNode proxy)
    {
        string displayNodeName = proxy.GetString("name") ?? "<未命名>";
        try
        {
            string name = proxy.GetRequiredString("name");
            Schema.Validate(proxy);
            return NodeConversionResult.Success(ConvertCore(proxy, name));
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail(
                $"{displayName} 节点 '{displayNodeName}' 解析失败 -> {ex.Message}");
        }
    }

    protected abstract ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name);
}
