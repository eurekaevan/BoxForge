using System.Collections;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeCatalogBuilderTests
{
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
