using System.Collections;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeCatalogBuilderTests
{
    [Test]
    public void CatalogContainsOnlyInterpretedNodes()
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        var catalog = builder.Build(new ClashConfig
        {
            Proxies = [CreateProxy("vless"), CreateProxy("trojan"), CreateProxy("anytls"), CreateProxy("hysteria2"), CreateProxy("ss")]
        });
        Assert.That(catalog.Nodes.Select(node => node.GetType()),
            Is.EqualTo(new[] { typeof(VlessNode), typeof(TrojanNode), typeof(AnyTlsNode), typeof(Hysteria2Node), typeof(ShadowsocksNode) }));
        Assert.That(catalog.Nodes.Select(node => node.Connection.ConnectTimeout), Is.All.Null);
        Assert.That(catalog.Names, Is.EqualTo(new[] { "vless-node", "trojan-node", "anytls-node", "hysteria2-node", "ss-node" }));
    }

    private static ClashProxyNode CreateProxy(string type)
    {
        var fields = new Hashtable
        {
            ["type"] = type,
            ["name"] = type + "-node",
            ["server"] = "node.example.com",
            ["port"] = 443,
            [type == "vless" ? "uuid" : "password"] = "test-only"
        };
        if (type == "ss") fields["cipher"] = "aes-128-gcm";
        return new ClashProxyNode(fields);
    }

    [Test]
    public void InvalidNodeRejectsInput()
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        var config = new ClashConfig
        {
            Proxies =
            [
                new ClashProxyNode(new Hashtable
                {
                    ["name"] = "sensitive-node-name",
                    ["type"] = "ss",
                    ["server"] = "node.example.com",
                    ["port"] = "invalid",
                    ["cipher"] = "aes-128-gcm",
                    ["password"] = "sensitive-password"
                })
            ]
        };

        var error = Assert.Throws<NodeParseException>(() => builder.Build(config));
        Assert.That(error!.Message, Does.Contain("port").And.Not.Contain("sensitive-password"));
    }

    [TestCase("🚀 PROXIES")]
    [TestCase("🇺🇸 US AUTO")]
    [TestCase("dns-direct-alidns")]
    [TestCase("response-domestic-primary")]
    [TestCase("response-priority-primary-a")]
    [TestCase("response-priority-secondary-a")]
    [TestCase("response-domestic-primary-a")]
    [TestCase("response-domestic-secondary-a")]
    [TestCase("response-global-primary-a")]
    [TestCase("response-global-secondary-a")]
    [TestCase("cn")]
    public void ReservedGeneratedTagNamesRejectInput(
        string reservedTag)
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        var config = new ClashConfig
        {
            Proxies =
            [
                new ClashProxyNode(new Hashtable
                {
                    ["name"] = reservedTag,
                    ["type"] = "ss",
                    ["server"] = "node.example.com",
                    ["port"] = 443,
                    ["cipher"] = "aes-128-gcm",
                    ["password"] = "test-only"
                })
            ]
        };

        var exception = Assert.Throws<NodeParseException>(() =>
            builder.Build(config));

        Assert.That(exception!.Message,
            Is.EqualTo($"节点名称 '{reservedTag}' 与 BoxForge 保留 tag 冲突"));
    }

    [TestCase(null)]
    [TestCase("unsupported")]
    public void MissingOrUnsupportedTypesRejectInput(string? type)
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        var properties = new Hashtable { ["name"] = "invalid-node" };
        if (type != null)
            properties["type"] = type;
        var config = new ClashConfig { Proxies = [new ClashProxyNode(properties)] };

        var error = Assert.Throws<NodeParseException>(() => builder.Build(config));
        Assert.That(error!.Message, Does.Contain("invalid-node")
            .And.Contain(type == null ? "缺少 type" : "不支持的类型: unsupported"));
    }
}
