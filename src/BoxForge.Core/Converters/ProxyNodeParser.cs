using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class ProxyNodeParser
{
    internal static ProxyNode Parse(ClashProxyNode proxy)
    {
        (string Name, SourceFieldSchema Schema, Func<ClashProxyNode, string, ProxyNode> Parse) definition = proxy.Type?.ToLowerInvariant() switch
        {
            "vless" => ("VLESS", VlessProtocol.Schema, VlessProtocol.ParseValidated),
            "trojan" => ("Trojan", TrojanProtocol.Schema, TrojanProtocol.ParseValidated),
            "hysteria2" => ("Hysteria2", Hysteria2Protocol.Schema, Hysteria2Protocol.ParseValidated),
            "ss" or "shadowsocks" => ("Shadowsocks", ShadowsocksProtocol.Schema, ShadowsocksProtocol.ParseValidated),
            "anytls" => ("AnyTLS", AnyTlsProtocol.Schema, AnyTlsProtocol.ParseValidated),
            null => throw new NodeParseException($"节点 '{proxy.Name}' 缺少 type 字段"),
            _ => throw new NodeParseException($"节点 '{proxy.Name}' 使用了不支持的类型: {proxy.Type}")
        };
        var (displayName, schema, parse) = definition;
        try
        {
            string name = proxy.GetRequiredString("name");
            schema.Validate(proxy);
            ProxyNode node = parse(proxy, name);
            node.Validate();
            return node;
        }
        catch (NodeParseException ex)
        {
            throw new NodeParseException($"{displayName} 节点 '{proxy.Name}' 解析失败 -> {ex.Message}");
        }
    }
}
