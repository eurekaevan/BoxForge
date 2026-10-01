using System.Collections;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Logging;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SemanticCoverageTests
{
    private const string RealityPublicKey =
        "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";

    [TestCase("vless")]
    [TestCase("trojan")]
    [TestCase("ss")]
    [TestCase("hysteria2")]
    [TestCase("anytls")]
    public void ExistingPlainNodesStillConvert(string protocol)
    {
        NodeConversionResult result = Converter(protocol).Convert(CreateNode(protocol));

        Assert.That(result, Is.TypeOf<ConvertedNode>());
    }

    [Test]
    public void ExistingRealityAndSafePluginInputsStillConvert()
    {
        ClashProxyNode reality = CreateNode("vless", new Hashtable
        {
            ["tls"] = true,
            ["client-fingerprint"] = "chrome",
            ["reality-opts"] = new Hashtable
            {
                ["public-key"] = RealityPublicKey,
                ["short-id"] = "00"
            }
        });
        ClashProxyNode plugin = CreateNode("ss", new Hashtable
        {
            ["plugin"] = "v2ray-plugin",
            ["plugin-opts"] = "tls;host=example.com"
        });

        Assert.Multiple(() =>
        {
            Assert.That(new VlessConverter().Convert(reality), Is.TypeOf<ConvertedNode>());
            Assert.That(new ShadowsocksConverter().Convert(plugin), Is.TypeOf<ConvertedNode>());
        });
    }

    [Test]
    public void StrictCatalogAcceptsExistingSupportedProtocolShapes()
    {
        var builder = new NodeCatalogBuilder(
            [new VlessConverter(), new TrojanConverter(),
                new ShadowsocksConverter(), new Hysteria2Converter(),
                new AnyTlsConverter()],
            new RecordingLogger<NodeCatalogBuilder>());
        ClashConfig config = new()
        {
            Proxies =
            [
                CreateNode("vless", new Hashtable { ["name"] = "vless-tcp" }),
                CreateNode("vless", new Hashtable
                {
                    ["name"] = "vless-reality",
                    ["reality-opts"] = new Hashtable
                    {
                        ["public-key"] = RealityPublicKey,
                        ["short-id"] = "00"
                    }
                }),
                CreateNode("trojan", new Hashtable { ["name"] = "trojan-tls" }),
                CreateNode("ss", new Hashtable { ["name"] = "ss-basic" }),
                CreateNode("ss", new Hashtable
                {
                    ["name"] = "ss-plugin", ["plugin"] = "v2ray-plugin"
                }),
                CreateNode("hysteria2", new Hashtable { ["name"] = "hy2-basic" }),
                CreateNode("anytls", new Hashtable { ["name"] = "anytls-basic" })
            ]
        };

        var catalog = builder.Build(config, strictNodeValidation: true);

        Assert.That(catalog.Outbounds, Has.Count.EqualTo(7));
    }

    [Test]
    public void Hysteria2PortsPreserveUpstreamPrecedenceOverPort()
    {
        var result = new Hysteria2Converter().Convert(CreateNode("hysteria2",
            new Hashtable { ["ports"] = "4000-5000" }));

        Assert.That(result, Is.TypeOf<ConvertedNode>());
        var outbound = (Hysteria2Outbound)((ConvertedNode)result).Outbound;
        Assert.Multiple(() =>
        {
            Assert.That(outbound.ServerPort, Is.Null);
            Assert.That(outbound.ServerPorts, Is.EqualTo(new[] { "4000:5000" }));
        });
    }

    [TestCase("vless", "network", "ws")]
    [TestCase("vless", "ws-opts", "ignored")]
    [TestCase("trojan", "network", "grpc")]
    [TestCase("trojan", "grpc-opts", "ignored")]
    [TestCase("hysteria2", "up", "50 Mbps")]
    [TestCase("hysteria2", "down", "200 Mbps")]
    [TestCase("hysteria2", "hop-interval", 20)]
    [TestCase("hysteria2", "bbr-profile", "aggressive")]
    [TestCase("anytls", "client-metadata", "value")]
    [TestCase("ss", "udp-over-tcp-version", 1)]
    [TestCase("ss", "network", "udp")]
    public void KnownUnmappedSemanticFieldsRejectWholeNode(
        string protocol,
        string field,
        object value)
    {
        NodeConversionResult result = Converter(protocol).Convert(CreateNode(protocol,
            new Hashtable { [field] = value }));

        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain(field));
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("test-node"));
    }

    [Test]
    public void NestedRealityUnknownFieldIsNotSilentlyDiscarded()
    {
        NodeConversionResult result = new VlessConverter().Convert(CreateNode(
            "vless", new Hashtable
            {
                ["reality-opts"] = new Hashtable
                {
                    ["public-key"] = RealityPublicKey,
                    ["short-id"] = "00",
                    ["future-key-exchange"] = true
                }
            }));

        Assert.That(((InvalidNode)result).ErrorMessage,
            Does.Contain("reality-opts.future-key-exchange"));
    }

    [Test]
    public void ConflictingAliasesCannotSilentlyShadowEachOther()
    {
        NodeConversionResult result = new VlessConverter().Convert(CreateNode(
            "vless", new Hashtable
            {
                ["packet-encoding"] = "xudp",
                ["packet_encoding"] = "packetaddr"
            }));

        Assert.That(((InvalidNode)result).ErrorMessage,
            Does.Contain("同时指定同一语义"));
    }

    [TestCase("udp-over-tcp", true, "UoT")]
    [TestCase("plugin", "obfs", "SIP003")]
    [TestCase("plugin-opts", "invalid", "没有插件")]
    public void UnsafeExistingShadowsocksMappingsFailFast(
        string field,
        object value,
        string reason)
    {
        NodeConversionResult result = new ShadowsocksConverter().Convert(
            CreateNode("ss", new Hashtable { [field] = value }));

        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain(reason));
    }

    [Test]
    public void StructuredPluginOptionsCannotBeFlattenedIntoDifferentSemantics()
    {
        NodeConversionResult result = new ShadowsocksConverter().Convert(
            CreateNode("ss", new Hashtable
            {
                ["plugin"] = "v2ray-plugin",
                ["plugin-opts"] = new Hashtable
                {
                    ["mode"] = "websocket",
                    ["tls"] = true
                }
            }));

        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage,
            Does.Contain("plugin-opts").And.Contain("尚未能保证"));
    }

    [Test]
    public void StrictModeReportsProtocolNodeAndUnknownField()
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder([new VlessConverter()], logger);
        ClashConfig config = new()
        {
            Proxies = [CreateNode("vless", new Hashtable
            {
                ["future-semantic-option"] = true
            })]
        };

        NodeParseException? exception = Assert.Throws<NodeParseException>(() =>
            builder.Build(config, strictNodeValidation: true));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("VLESS"));
            Assert.That(exception.Message, Does.Contain("test-node"));
            Assert.That(exception.Message, Does.Contain("future-semantic-option"));
            Assert.That(logger.Messages, Is.Empty);
        });
    }

    [Test]
    public void NonStrictModeSkipsUnsafeNodeAndKeepsValidNode()
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder([new VlessConverter()], logger);
        ClashConfig config = new()
        {
            Proxies =
            [
                CreateNode("vless", new Hashtable
                {
                    ["network"] = "ws",
                    ["ws-opts"] = new Hashtable { ["path"] = "/foo" }
                }),
                CreateNode("vless", new Hashtable { ["name"] = "safe-node" })
            ]
        };

        var catalog = builder.Build(config, strictNodeValidation: false);

        Assert.Multiple(() =>
        {
            Assert.That(catalog.Outbounds.Select(node => node.Tag),
                Is.EqualTo(new[] { "safe-node" }));
            Assert.That(logger.Messages, Has.Count.EqualTo(1));
            Assert.That(logger.Messages[0], Does.Contain("test-node"));
            Assert.That(logger.Messages[0], Does.Contain("network"));
        });
    }

    [Test]
    public void SafeMetadataAndInertOptionsAreExplicitlyAccepted()
    {
        ClashProxyNode node = CreateNode("vless", new Hashtable
        {
            ["metadata"] = new Hashtable { ["display-color"] = "blue" },
            ["network"] = "tcp",
            ["udp"] = true,
            ["tfo"] = false,
            ["ip-version"] = "ipv4",
            ["smux"] = new Hashtable { ["enabled"] = false }
        });

        Assert.That(new VlessConverter().Convert(node), Is.TypeOf<ConvertedNode>());
    }

    [TestCase("udp", false)]
    [TestCase("tfo", true)]
    [TestCase("ip-version", "dual")]
    [TestCase("sni", "example.com")]
    public void CommonExplicitConnectionSettingsCannotBeSilentlyIgnored(
        string field,
        object value)
    {
        NodeConversionResult result = new VlessConverter().Convert(CreateNode(
            "vless", new Hashtable { [field] = value }));

        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain(field));
    }

    [Test]
    public void Hysteria2RejectsExplicitUtlsFingerprint()
    {
        NodeConversionResult result = new Hysteria2Converter().Convert(
            CreateNode("hysteria2", new Hashtable
            {
                ["client-fingerprint"] = "chrome"
            }));

        Assert.That(((InvalidNode)result).ErrorMessage,
            Does.Contain("client-fingerprint"));
    }

    [TestCase("server")]
    [TestCase("uuid")]
    [TestCase("sni")]
    public void MappedScalarFieldsRejectStructuredValues(string field)
    {
        NodeConversionResult result = new VlessConverter().Convert(CreateNode(
            "vless", new Hashtable
            {
                ["tls"] = true,
                [field] = new List<string> { "invalid", "shape" }
            }));

        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage,
            Does.Contain($"字段 '{field}' 必须是标量值"));
    }

    [Test]
    public void RawModelDoesNotSerializeBoxForgeTuning()
    {
        Hysteria2Outbound raw = new()
        {
            Tag = "raw",
            Server = "node.example.com",
            ServerPort = 443,
            Password = "test-only"
        };
        string json = Serialize(raw);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("connect_timeout"));
            Assert.That(json, Does.Not.Contain("tcp_keep_alive"));
            Assert.That(json, Does.Not.Contain("hop_interval"));
            Assert.That(json, Does.Not.Contain("bbr_profile"));
        });
    }

    [Test]
    public void TuningFillsOnlyAbsentFields()
    {
        Hysteria2Outbound source = new()
        {
            Tag = "source",
            Server = "node.example.com",
            ServerPort = 443,
            Password = "test-only",
            ConnectTimeout = "9s",
            TcpKeepAlive = "2m",
            TcpKeepAliveInterval = "12s",
            HopInterval = "20s",
            HopIntervalMax = "40s",
            BbrProfile = "conservative"
        };
        var linux = (Hysteria2Outbound)OutboundTuningPolicy.Apply(
            source, TargetPlatform.Linux);
        var android = (Hysteria2Outbound)OutboundTuningPolicy.Apply(
            source with { TcpKeepAlive = null }, TargetPlatform.Android);

        Assert.Multiple(() =>
        {
            Assert.That(linux.ConnectTimeout, Is.EqualTo("9s"));
            Assert.That(linux.TcpKeepAlive, Is.EqualTo("2m"));
            Assert.That(linux.TcpKeepAliveInterval, Is.EqualTo("12s"));
            Assert.That(linux.HopInterval, Is.EqualTo("20s"));
            Assert.That(linux.HopIntervalMax, Is.EqualTo("40s"));
            Assert.That(linux.BbrProfile, Is.EqualTo("conservative"));
            Assert.That(android.TcpKeepAlive, Is.Null);
            Assert.That(android.TcpKeepAliveInterval, Is.EqualTo("12s"));
            Assert.That(android.ConnectTimeout, Is.EqualTo("9s"));
            Assert.That(source.HopIntervalMax, Is.EqualTo("40s"));
        });
    }

    private static IProxyConverter Converter(string protocol) => protocol switch
    {
        "vless" => new VlessConverter(),
        "trojan" => new TrojanConverter(),
        "ss" => new ShadowsocksConverter(),
        "hysteria2" => new Hysteria2Converter(),
        "anytls" => new AnyTlsConverter(),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static ClashProxyNode CreateNode(
        string protocol,
        Hashtable? extra = null)
    {
        var values = new Hashtable
        {
            ["name"] = "test-node",
            ["type"] = protocol,
            ["server"] = "node.example.com",
            ["port"] = 443
        };
        values[protocol switch
        {
            "vless" => "uuid",
            "ss" => "cipher",
            _ => "password"
        }] = protocol == "vless"
            ? "00000000-0000-4000-8000-000000000001"
            : protocol == "ss" ? "aes-128-gcm" : "test-only";
        if (protocol == "ss")
        {
            values["password"] = "test-only";
        }

        if (extra is not null)
        {
            foreach (DictionaryEntry entry in extra)
            {
                values[entry.Key] = entry.Value;
            }
        }

        return new ClashProxyNode(values);
    }

    private static string Serialize(Outbound outbound) =>
        new ConfigSerializer().Serialize(new SingboxConfig
        {
            Outbounds = [outbound]
        });

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
