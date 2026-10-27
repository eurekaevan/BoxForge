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
    [TestCase("unknown-secret-key", "source.proxies[1].[unknown]")]
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
            Assert.That(diagnostic.Code, Is.EqualTo("SRC002"));
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

    [TestCase("ss", "name", null, "name")]
    [TestCase("ss", "server", null, "server")]
    [TestCase("ss", "port", "sensitive-port", "port")]
    [TestCase("ss", "password", "", "password")]
    [TestCase("ss", "cipher", "", "cipher")]
    [TestCase("ss", "udp-over-tcp", "sensitive-bool", "udp-over-tcp")]
    [TestCase("ss", "udp_over_tcp", "sensitive-bool", "udp_over_tcp")]
    [TestCase("ss", "udp-over-tcp-version", 3, "udp-over-tcp-version")]
    [TestCase("ss", "plugin-opts.mode", "sensitive-mode", "plugin-opts.mode")]
    [TestCase("ss", "plugin_opts.host", "sensitive\nhost", "plugin_opts.host")]
    [TestCase("ss", "plugin_opts.sensitive-key", "sensitive-value", "plugin_opts.[unknown]")]
    [TestCase("ss", "smux.enabled", "sensitive-bool", "smux.enabled")]
    [TestCase("vless", "uuid", null, "uuid")]
    [TestCase("vless", "flow", "sensitive-unsupported-flow", "flow")]
    [TestCase("vless", "tls", "sensitive-bool", "tls")]
    [TestCase("vless", "sni", "node.example.com", "tls")]
    [TestCase("trojan", "skip-cert-verify", "sensitive-bool", "skip-cert-verify")]
    [TestCase("trojan", "client_fingerprint", "sensitive-fingerprint", "client_fingerprint")]
    [TestCase("trojan", "reality-opts.public-key", "sensitive-key", "reality-opts.public-key")]
    [TestCase("trojan", "reality_opts.short_id", "sensitive-id", "reality_opts.short_id")]
    [TestCase("anytls", "min-idle-session", -1, "min-idle-session")]
    [TestCase("anytls", "min_idle_session", "sensitive-count", "min_idle_session")]
    [TestCase("anytls", "client-metadata", 123, "client-metadata")]
    [TestCase("anytls", "client_metadata", "sensitive\nmetadata", "client_metadata")]
    [TestCase("anytls", "idle_timeout", "sensitive-duration", "idle_timeout")]
    [TestCase("hysteria2", "up", "sensitive-bandwidth", "up")]
    [TestCase("hysteria2", "down", "100 Kbps", "down")]
    [TestCase("hysteria2", "ports", "443,,8443", "ports")]
    [TestCase("hysteria2", "ports", "sensitive-port", "ports")]
    [TestCase("hysteria2", "ports", "443-1", "ports")]
    [TestCase("hysteria2", "hop_interval", "sensitive-interval", "hop_interval")]
    [TestCase("hysteria2", "bbr_profile", "sensitive-profile", "bbr_profile")]
    [TestCase("hysteria2", "obfs_min_packet_size", -1, "obfs_min_packet_size")]
    [TestCase("hysteria2", "obfs-password", "", "obfs-password")]
    public void InterpretedFieldDiagnosticsPreserveAliasesNestedPathsAndNodeIndices(
        string protocol, string field, object? value, string expectedField)
    {
        var fields = new Hashtable
        {
            ["name"] = "sensitive-node",
            ["type"] = protocol,
            ["server"] = "node.example.com",
            ["port"] = 443
        };
        if (protocol == "vless") fields["uuid"] = "00000000-0000-4000-8000-000000000001";
        else fields["password"] = "sensitive-password";
        if (protocol == "ss") fields["cipher"] = "aes-128-gcm";
        if (field.StartsWith("plugin", StringComparison.Ordinal)) fields["plugin"] = "obfs";
        if (field == "obfs-password") fields["obfs"] = "salamander";
        if (field.StartsWith("reality", StringComparison.Ordinal)) fields["client-fingerprint"] = "chrome";
        // Exercise errors discovered while a different field is being checked.
        if (field == "sni") fields["tls"] = "sensitive-bool";
        string[] path = field.Split('.');
        if (path.Length == 2)
        {
            var nested = new Hashtable();
            if (path[0].StartsWith("reality", StringComparison.Ordinal))
            {
                nested["public-key"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
                if (path[1] == "public-key") nested.Remove("public-key");
            }
            nested[path[1]] = value;
            fields[path[0]] = nested;
        }
        else fields[field] = value;
        var valid = new Hashtable
        {
            ["name"] = "safe",
            ["type"] = "trojan",
            ["server"] = "node.example.com",
            ["port"] = 443,
            ["password"] = "test-only"
        };
        var config = new ClashConfig { Proxies = [new(valid), new(fields)] };

        var error = Assert.Throws<NodeParseException>(() => NodeCatalogBuilder.Build(config))!;

        Assert.Multiple(() =>
        {
            Assert.That(error.Diagnostics.Single(), Is.EqualTo(new ConfigDiagnostic("SRC002",
                $"source.proxies[1].{expectedField}", "来源字段不受支持、存在冲突或值无效。")));
            Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("sensitive"));
        });
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
        var exception = Assert.Throws<ConfigValidationException>(() =>
            ConfigurationPlanner.Plan(nodes, BoxForgeProfile.Default.Resolve(TargetPlatform.Linux)));

        Assert.That(exception!.Diagnostics, Is.EqualTo(new[]
        {
            new ConfigDiagnostic("IR009", "plan.nodes[0].name", "节点名称与保留名称冲突。")
        }));
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
