using BoxForge.Models;
using BoxForge.Builders;
using System.Collections;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using BoxForge.Parsers;
using System.Text.Json;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeCatalogBuilderTests
{
    [TestCase("network", "source.proxies[1].network")]
    [TestCase("unknown-secret-key", "source.proxies[1]")]
    [TestCase("type", "source.proxies[1].type")]
    public void SourceDiagnosticsLocateNodesWithoutExposingInput(string field, string expectedPath)
    {
        var valid = new Hashtable
        {
            ["name"] = "safe",
            ["type"] = "trojan",
            ["server"] = "node.example.com",
            ["port"] = 443,
            ["password"] = "password-secret"
        };
        var invalid = (Hashtable)valid.Clone();
        invalid["name"] = "node-secret";
        invalid[field] = "value-secret";
        var config = new ClashConfig { Proxies = [new(valid), new(invalid)] };

        var error = Assert.Throws<NodeParseException>(() => NodeCatalogBuilder.Build(config))!;
        var diagnostic = error.Diagnostics.Single();
        string serialized = JsonSerializer.Serialize(error.Diagnostics);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Path, Is.EqualTo(expectedPath));
            Assert.That(diagnostic.Code, Is.EqualTo(field == "unknown-secret-key" ? "SRC001" : "SRC002"));
            Assert.That(serialized, Does.Not.Contain("secret"));
            Assert.That(error.Message, Does.Contain("node-secret"));
            Assert.That(error.InnerException, Is.TypeOf<NodeParseException>());
        });
    }

    [Test]
    public void KnownNestedFieldDiagnosticPreservesDeclaredPath()
    {
        var config = new ClashConfig
        {
            Proxies = [new(new Hashtable
            {
                ["name"] = "node-secret", ["type"] = "trojan",
                ["reality-opts"] = new Hashtable { ["support-x25519mlkem768"] = "value-secret" }
            })]
        };

        var error = Assert.Throws<NodeParseException>(() => NodeCatalogBuilder.Build(config))!;
        Assert.That(error.Diagnostics.Single(), Is.EqualTo(new ConfigDiagnostic("SRC002",
            "source.proxies[0].reality-opts.support-x25519mlkem768", "来源字段不受支持、存在冲突或值无效。")));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("secret"));
    }

    [Test]
    public void LegacyExceptionMessageDoesNotBecomePublicDiagnostic()
    {
        var error = new NodeParseException("password-secret");
        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Is.EqualTo("password-secret"));
            Assert.That(error.Diagnostics.Single().Path, Is.EqualTo("source"));
            Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("password-secret"));
            Assert.Throws<NotSupportedException>(() =>
                ((IList<ConfigDiagnostic>)error.Diagnostics)[0] = new ConfigDiagnostic("other", "other", "other"));
        });
    }

    [TestCase("proxies: [null]", "source.proxies[0]")]
    [TestCase("proxies: [&node {nested: *node}]", "source.proxies[0]")]
    public void InvalidYamlNodeStructureHasSafeIndexedDiagnostic(string yaml, string path)
    {
        var error = Assert.Throws<NodeParseException>(() => new ClashParser().Parse(yaml))!;
        Assert.That(error.Diagnostics.Single().Path, Is.EqualTo(path));
        Assert.That(error.Diagnostics.Single().Code, Is.EqualTo("SRC001"));
    }

    [Test]
    public void InvalidNodeRejectsInput()
    {
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

        var error = Assert.Throws<NodeParseException>(() => NodeCatalogBuilder.Build(config));
        Assert.That(error!.Message, Does.Contain("port").And.Not.Contain("sensitive-password"));
    }

    [TestCase("🚀 PROXIES")]
    [TestCase("🇺🇸 US AUTO")]
    [TestCase("dns-direct-alidns")]
    [TestCase("dns-service-ai-cloudflare")]
    [TestCase("dns-service-games-google")]
    [TestCase("response-service-games-primary")]
    [TestCase("response-domestic-primary")]
    [TestCase("response-service-ai-primary-a")]
    [TestCase("response-service-ai-secondary-a")]
    [TestCase("response-domestic-primary-a")]
    [TestCase("response-domestic-secondary-a")]
    [TestCase("response-global-primary-a")]
    [TestCase("response-global-secondary-a")]
    [TestCase("cn")]
    public void ReservedGeneratedTagNamesBindOnlyDuringPlanning(
        string reservedTag)
    {
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

        var nodes = NodeCatalogBuilder.Build(config);
        Assert.That(nodes.Names, Is.EqualTo(new[] { reservedTag }));
        var exception = Assert.Throws<NodeParseException>(() =>
            ConfigurationPlanner.Plan(nodes, BoxForgeProfile.Default.Resolve(TargetPlatform.Linux)));

        Assert.That(exception!.Message,
            Is.EqualTo($"节点名称 '{reservedTag}' 与 BoxForge 保留 tag 冲突"));
    }

    [TestCase(null)]
    [TestCase("unsupported")]
    public void MissingOrUnsupportedTypesRejectInput(string? type)
    {
        var properties = new Hashtable { ["name"] = "invalid-node" };
        if (type != null)
            properties["type"] = type;
        var config = new ClashConfig { Proxies = [new ClashProxyNode(properties)] };

        var error = Assert.Throws<NodeParseException>(() => NodeCatalogBuilder.Build(config));
        Assert.That(error!.Message, Does.Contain("invalid-node")
            .And.Contain(type == null ? "缺少 type" : "不支持的类型: unsupported"));
    }
}
