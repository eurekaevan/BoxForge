using BoxForge.Exceptions;
using BoxForge.Configuration;
using BoxForge.Models.Clash;
using BoxForge.Models;
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
            null => throw new NodeParseException($"节点 '{proxy.Name}' 缺少 type 字段",
                new ConfigDiagnostic("SRC002", "source.type", "节点协议类型缺失或不受支持。")),
            _ => throw new NodeParseException($"节点 '{proxy.Name}' 使用了不支持的类型: {proxy.Type}",
                new ConfigDiagnostic("SRC002", "source.type", "节点协议类型缺失或不受支持。"))
        };
        var (displayName, schema, parse) = definition;
        try
        {
            string name = proxy.GetRequiredString("name");
            schema.Validate(proxy);
            ProxyNode node = parse(proxy, name) with
            {
                AddressFamily = proxy.GetPresentField("ip-version") is null ? null : AddressFamilyPolicy.Ipv4Only
            };
            node.Validate();
            return node;
        }
        catch (NodeParseException ex)
        {
            throw new NodeParseException($"{displayName} 节点 '{proxy.Name}' 解析失败 -> {ex.Message}",
                ex.Diagnostics[0], ex);
        }
    }
}
