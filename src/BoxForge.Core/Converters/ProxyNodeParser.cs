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
        string? type = new NodeFieldReader(proxy).String("type");
        (string Name, SourceFieldSchema Schema, Func<NodeFieldReader, string, ProxyNode> Parse) definition = type?.ToLowerInvariant() switch
        {
            "vless" => ("VLESS", VlessProtocol.Schema, VlessProtocol.Parse),
            "trojan" => ("Trojan", TrojanProtocol.Schema, TrojanProtocol.Parse),
            "hysteria2" => ("Hysteria2", Hysteria2Protocol.Schema, Hysteria2Protocol.Parse),
            "ss" or "shadowsocks" => ("Shadowsocks", ShadowsocksProtocol.Schema, ShadowsocksProtocol.Parse),
            "anytls" => ("AnyTLS", AnyTlsProtocol.Schema, AnyTlsProtocol.Parse),
            null => throw new NodeParseException($"节点 '{proxy.Name ?? "<未命名>"}' 缺少 type 字段",
                new ConfigDiagnostic("SRC002", "source.type", "节点协议类型缺失或不受支持。")),
            _ => throw new NodeParseException($"节点 '{proxy.Name ?? "<未命名>"}' 使用了不支持的类型: {type}",
                new ConfigDiagnostic("SRC002", "source.type", "节点协议类型缺失或不受支持。"))
        };
        var (displayName, schema, parse) = definition;
        string name = "<未命名>";
        try
        {
            NodeFieldReader reader = schema.Bind(proxy, out name);
            ProxyNode node = parse(reader, name) with
            {
                AddressFamily = reader.PresentField("ip-version") is null ? null : AddressFamilyPolicy.Ipv4Only
            };
            node.Validate();
            return node;
        }
        catch (NodeParseException ex)
        {
            throw new NodeParseException($"{displayName} 节点 '{name}' 解析失败 -> {ex.Message}",
                ex.Diagnostics[0], ex);
        }
    }
}
