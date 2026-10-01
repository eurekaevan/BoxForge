using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoxForge.Tests;

[TestFixture]
public sealed class TransportMappingTests
{
    private const string PublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";

    [TestCase("vless", null)]
    [TestCase("vless", "tcp")]
    [TestCase("trojan", null)]
    [TestCase("trojan", "tcp")]
    public void PlainTcpOmitsTransport(string protocol, string? network)
    {
        Hashtable fields = [];
        if (network is not null) fields["network"] = network;
        JsonElement json = Serialize(Convert(protocol, fields));
        Assert.That(json.TryGetProperty("transport", out _), Is.False);
    }

    [TestCase("vless", false)]
    [TestCase("trojan", false)]
    [TestCase("vless", true)]
    [TestCase("trojan", true)]
    public void WebSocketMapsSourceValuesAndAliases(string protocol, bool underscore)
    {
        string Key(string name) => underscore ? name.Replace('-', '_') : name;
        var outbound = Convert(protocol, new Hashtable
        {
            ["network"] = "ws",
            [Key("ws-opts")] = new Hashtable
            {
                ["path"] = "/foo",
                ["headers"] = new Hashtable { ["Host"] = "ws.example.com", ["X-Custom"] = "  exact  " },
                [Key("max-early-data")] = 2048,
                [Key("early-data-header-name")] = "Sec-WebSocket-Protocol"
            }
        });
        JsonElement transport = Serialize(outbound).GetProperty("transport");
        Assert.Multiple(() =>
        {
            Assert.That(transport.GetProperty("type").GetString(), Is.EqualTo("ws"));
            Assert.That(transport.GetProperty("path").GetString(), Is.EqualTo("/foo"));
            Assert.That(transport.GetProperty("headers").GetProperty("X-Custom").GetString(), Is.EqualTo("  exact  "));
            Assert.That(transport.GetProperty("headers").GetProperty("Host").GetString(), Is.EqualTo("ws.example.com"));
            Assert.That(transport.GetProperty("max_early_data").GetUInt32(), Is.EqualTo(2048));
            Assert.That(transport.GetProperty("early_data_header_name").GetString(), Is.EqualTo("Sec-WebSocket-Protocol"));
        });
    }

    [TestCase("vless", "ws.example.com")]
    [TestCase("trojan", "node.example.com")]
    public void WebSocketSniFallbackMatchesSource(string protocol, string expected)
    {
        JsonElement json = Serialize(Convert(protocol, new Hashtable
        {
            ["network"] = "ws",
            ["ws-opts"] = new Hashtable { ["headers"] = new Hashtable { ["Host"] = "ws.example.com" } }
        }));
        Assert.That(json.GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo(expected));
    }

    [Test]
    public void VlessProtocolCaseDoesNotChangeWebSocketFallback()
    {
        JsonElement json = Serialize(Convert("vless", new Hashtable
        {
            ["type"] = "VLESS",
            ["network"] = "ws",
            ["ws-opts"] = new Hashtable { ["headers"] = new Hashtable { ["Host"] = "ws.example.com" } }
        }));
        Assert.That(json.GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("ws.example.com"));
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    public void WebSocketDoesNotInventPathOrEarlyData(string protocol)
    {
        JsonElement transport = Serialize(Convert(protocol, new Hashtable { ["network"] = "ws" })).GetProperty("transport");
        Assert.Multiple(() =>
        {
            Assert.That(transport.TryGetProperty("path", out _), Is.False);
            Assert.That(transport.TryGetProperty("max_early_data", out _), Is.False);
            Assert.That(transport.TryGetProperty("early_data_header_name", out _), Is.False);
            Assert.That(transport.GetProperty("headers").GetProperty("Host").GetString(), Is.EqualTo("node.example.com"));
        });
    }

    [Test]
    public void PlainWebSocketNeedsExplicitHostToAvoidReplacingSourceRandomHost()
    {
        ProxyOutbound outbound = Convert("vless", new Hashtable
        {
            ["tls"] = false,
            ["network"] = "ws",
            ["ws-opts"] = new Hashtable { ["headers"] = new Hashtable { ["Host"] = "ws.example.com" } }
        });
        JsonElement json = Serialize(outbound);
        Assert.Multiple(() =>
        {
            Assert.That(json.TryGetProperty("tls", out _), Is.False);
            Assert.That(json.GetProperty("transport").GetProperty("headers").GetProperty("Host").GetString(), Is.EqualTo("ws.example.com"));
        });
        NodeConversionResult invalid = new VlessConverter().Convert(Node("vless", new Hashtable { ["tls"] = false, ["network"] = "ws" }));
        Assert.That(invalid, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)invalid).ErrorMessage, Does.Contain("ws-opts.headers.Host"));
    }

    [TestCase("vless", false)]
    [TestCase("vless", true)]
    public void GrpcMapsOrdinaryServiceName(string protocol, bool underscore)
    {
        JsonElement transport = Serialize(Convert(protocol, new Hashtable
        {
            ["network"] = "grpc",
            [underscore ? "grpc_opts" : "grpc-opts"] = new Hashtable
            {
                [underscore ? "grpc_service_name" : "grpc-service-name"] = "example.service"
            }
        })).GetProperty("transport");
        Assert.Multiple(() =>
        {
            Assert.That(transport.GetProperty("type").GetString(), Is.EqualTo("grpc"));
            Assert.That(transport.GetProperty("service_name").GetString(), Is.EqualTo("example.service"));
            Assert.That(transport.EnumerateObject().Count(), Is.EqualTo(2));
        });
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    public void HttpUpgradeMapsRawUpgradeNotWebSocket(string protocol)
    {
        JsonElement transport = Serialize(Convert(protocol, new Hashtable
        {
            ["network"] = "ws",
            ["ws-opts"] = new Hashtable
            {
                ["path"] = "/upgrade",
                ["headers"] = new Hashtable { ["Host"] = "upgrade.example.com", ["X-Custom"] = "unchanged" },
                ["v2ray-http-upgrade"] = true,
                ["v2ray-http-upgrade-fast-open"] = false
            }
        })).GetProperty("transport");
        Assert.Multiple(() =>
        {
            Assert.That(transport.GetProperty("type").GetString(), Is.EqualTo("httpupgrade"));
            Assert.That(transport.GetProperty("host").GetString(), Is.EqualTo("upgrade.example.com"));
            Assert.That(transport.GetProperty("path").GetString(), Is.EqualTo("/upgrade"));
            Assert.That(transport.GetProperty("headers").GetProperty("X-Custom").GetString(), Is.EqualTo("unchanged"));
            Assert.That(transport.GetProperty("headers").TryGetProperty("Host", out _), Is.False);
        });
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    [TestCase("anytls")]
    [TestCase("hysteria2")]
    public void SharedAlpnPreservesOrderDuplicatesAndAbsence(string protocol)
    {
        JsonElement present = Serialize(Convert(protocol, new Hashtable { ["alpn"] = new[] { "h2", "http/1.1", "h2" } }));
        JsonElement absent = Serialize(Convert(protocol, []));
        Assert.Multiple(() =>
        {
            Assert.That(present.GetProperty("tls").GetProperty("alpn").EnumerateArray().Select(item => item.GetString()),
                Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
            Assert.That(absent.GetProperty("tls").TryGetProperty("alpn", out _), Is.False);
        });
    }

    [Test]
    public void RealityAndUtlsRetainAlpnOnGrpcAndTuningPreservesWholeTransport()
    {
        ProxyOutbound source = Convert("vless", new Hashtable
        {
            ["network"] = "grpc",
            ["alpn"] = new[] { "h2" },
            ["grpc-opts"] = new Hashtable { ["grpc-service-name"] = "example" },
            ["client-fingerprint"] = "chrome",
            ["reality-opts"] = new Hashtable { ["public-key"] = PublicKey, ["short-id"] = "00" }
        }) with
        { ConnectTimeout = "9s", TcpKeepAlive = "2m" };
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
        {
            JsonElement tuned = Serialize(OutboundTuningPolicy.Apply(source, platform));
            Assert.Multiple(() =>
            {
                Assert.That(tuned.GetProperty("tls").GetProperty("alpn")[0].GetString(), Is.EqualTo("h2"));
                Assert.That(tuned.GetProperty("tls").GetProperty("utls").GetProperty("fingerprint").GetString(), Is.EqualTo("chrome"));
                Assert.That(tuned.GetProperty("tls").GetProperty("reality").GetProperty("public_key").GetString(), Is.EqualTo(PublicKey));
                Assert.That(tuned.GetProperty("transport").GetProperty("service_name").GetString(), Is.EqualTo("example"));
                Assert.That(tuned.GetProperty("connect_timeout").GetString(), Is.EqualTo("9s"));
                Assert.That(tuned.GetProperty("tcp_keep_alive").GetString(), Is.EqualTo("2m"));
            });
        }
    }

    private static IEnumerable<TestCaseData> InvalidInputs()
    {
        yield return Invalid("ws", "ws-opts", new Hashtable { ["future-field"] = true }, "ws-opts.future-field");
        foreach (string field in new[] { "grpc-user-agent", "ping-interval", "max-connections", "min-streams", "max-streams" })
            yield return Invalid("grpc", "grpc-opts", new Hashtable { ["grpc-service-name"] = "example", [field] = 1 }, $"grpc-opts.{field}");
        foreach (string? service in new string?[] { null, "", "/custom/Tun", "a/b", "a%2Fb" })
            yield return Invalid("grpc", "grpc-opts", new Hashtable { ["grpc-service-name"] = service }, "grpc-opts.grpc-service-name");
        foreach (object header in new object[] { new[] { "one", "two" }, 123, new Hashtable() })
            yield return Invalid("ws", "ws-opts", new Hashtable { ["headers"] = new Hashtable { ["X-Test"] = header } }, "ws-opts.headers");
        yield return Invalid("ws", "ws-opts", new Hashtable { ["headers"] = new Hashtable { ["Host"] = "one", ["host"] = "two" } }, "ws-opts.headers");
        foreach (object size in new object[] { -1, 1.5, "bad", "4294967296" })
            yield return Invalid("ws", "ws-opts", new Hashtable { ["max-early-data"] = size }, "ws-opts.max-early-data");
        yield return Invalid("ws", "ws-opts", new Hashtable { ["v2ray-http-upgrade-fast-open"] = true }, "ws-opts.v2ray-http-upgrade-fast-open");
        yield return Invalid("ws", "ws-opts", new Hashtable { ["v2ray-http-upgrade"] = "bad" }, "ws-opts.v2ray-http-upgrade");
        yield return Invalid("ws", "ws-opts", new Hashtable { ["v2ray-http-upgrade"] = true, ["max-early-data"] = 2048 }, "ws-opts.max-early-data");
        foreach (string path in new[] { "/ws?ed=2048", "/ws?token=one", "/ws#fragment", "/encoded%2Fpath", "//authority/path", "scheme:opaque" })
            yield return Invalid("ws", "ws-opts", new Hashtable { ["path"] = path }, "ws-opts.path");
        yield return Invalid("tcp", "ws-opts", new Hashtable { ["path"] = "/ignored" }, "ws-opts");
        yield return Invalid("ws", "ws_opts", new Hashtable(), "ws_opts", new Hashtable { ["ws-opts"] = new Hashtable() });
        yield return Invalid("grpc", "grpc-opts", new Hashtable { ["grpc-service-name"] = "one", ["grpc_service_name"] = "two" }, "grpc-opts.grpc_service_name");
        foreach (string network in new[] { "http", "h2", "xhttp", "unknown" })
            yield return Invalid(network, "tls", true, "network");
        foreach (string field in new[] { "fingerprint", "name-cert-verify", "encryption", "xhttp-opts" })
            yield return Invalid("tcp", field, "nonempty", field);
        yield return Invalid("ws", "reality-opts", new Hashtable { ["public-key"] = PublicKey, ["short-id"] = "00" }, "reality-opts");
        yield return Invalid("ws", "alpn", new[] { "h2", "http/1.1" }, "alpn");
        yield return Invalid("grpc", "alpn", Array.Empty<string>(), "alpn");
        yield return Invalid("grpc", "sni", "explicit.example.com", "sni/servername");
        yield return Invalid("grpc", "servername", "explicit.example.com", "sni/servername");
        yield return Invalid("tcp", "alpn", "h2", "alpn");
        yield return Invalid("tcp", "alpn", new object[] { 1 }, "alpn");
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    public void RealityWebSocketAndTrojanExtraLayerAreStillRejected(string protocol)
    {
        IProxyConverter converter = protocol == "vless" ? new VlessConverter() : new TrojanConverter();
        NodeConversionResult reality = converter.Convert(Node(protocol, new Hashtable
        {
            ["network"] = "ws",
            ["reality-opts"] = new Hashtable { ["public-key"] = PublicKey, ["short-id"] = "00" }
        }));
        Assert.That(reality, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)reality).ErrorMessage, Does.Contain("reality-opts"));
        if (protocol == "trojan")
        {
            NodeConversionResult ss = converter.Convert(Node(protocol, new Hashtable { ["ss-opts"] = new Hashtable { ["enabled"] = true } }));
            Assert.That(ss, Is.TypeOf<InvalidNode>());
            Assert.That(((InvalidNode)ss).ErrorMessage, Does.Contain("ss-opts"));
        }
    }

    [Test]
    public void TrojanUtlsWebSocketRejectsAlpnThatMihomoOverrides()
    {
        NodeConversionResult result = new TrojanConverter().Convert(Node("trojan", new Hashtable
        {
            ["network"] = "ws",
            ["client-fingerprint"] = "chrome",
            ["alpn"] = new[] { "h2" }
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("alpn"));
    }

    [TestCase("Host")]
    [TestCase("Sec-WebSocket-Key")]
    [TestCase("Invalid Header")]
    public void EarlyDataCannotOverrideReservedHandshakeHeaders(string header)
    {
        NodeConversionResult result = new VlessConverter().Convert(Node("vless", new Hashtable
        {
            ["network"] = "ws",
            ["ws-opts"] = new Hashtable { ["max-early-data"] = 2048, ["early-data-header-name"] = header }
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("ws-opts.early-data-header-name"));
    }

    [TestCaseSource(nameof(InvalidInputs))]
    public void UnsupportedOrAmbiguousSemanticsFailStrictAndSkipWholeNode(Hashtable fields, string expectedPath)
    {
        ClashProxyNode unsafeNode = Node("vless", fields);
        var builder = new NodeCatalogBuilder([new VlessConverter()], NullLogger<NodeCatalogBuilder>.Instance);
        var config = new ClashConfig { Proxies = [unsafeNode, Node("vless", new Hashtable { ["name"] = "safe" })] };
        NodeParseException? error = Assert.Throws<NodeParseException>(() => builder.Build(config, strictNodeValidation: true));
        Assert.That(error!.Message, Does.Contain(expectedPath));
        Assert.That(builder.Build(config, strictNodeValidation: false).Names, Is.EqualTo(new[] { "safe" }));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task EngineGeneratesRepresentativeFixtureWithoutChangingSourceSemantics(TargetPlatform platform)
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddLogging().AddBoxForge(new ConfigurationBuilder().Build()).BuildServiceProvider();
        string yaml = await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "transport.yaml"));
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>()
            .ConvertAsync(new ConversionRequest("transports", yaml, [platform]));
        using JsonDocument document = JsonDocument.Parse(bundle.Artifacts.Single().Content);
        var nodes = document.RootElement.GetProperty("outbounds").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() is "vless" or "trojan")
            .ToDictionary(item => item.GetProperty("tag").GetString()!);
        Assert.Multiple(() =>
        {
            Assert.That(nodes, Has.Count.EqualTo(8));
            Assert.That(nodes["vless-ws"].GetProperty("transport").GetProperty("max_early_data").GetInt32(), Is.EqualTo(2048));
            Assert.That(nodes["vless-ws"].GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("ws.example.com"));
            foreach (string name in new[] { "vless-grpc", "vless-reality-grpc" })
                Assert.That(nodes[name].GetProperty("transport").GetProperty("service_name").GetString(), Is.EqualTo("example"));
            foreach (string name in new[] { "vless-httpupgrade", "trojan-httpupgrade" })
                Assert.That(nodes[name].GetProperty("transport").GetProperty("type").GetString(), Is.EqualTo("httpupgrade"));
            Assert.That(nodes["vless-alpn"].GetProperty("tls").GetProperty("alpn").EnumerateArray().Select(item => item.GetString()),
                Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
        });
    }

    [TestCase("grpc-opts")]
    [TestCase("grpc_opts")]
    public void TrojanGrpcFailsRatherThanChangingAuthority(string optionsKey)
    {
        NodeConversionResult result = new TrojanConverter().Convert(Node("trojan", new Hashtable
        {
            ["network"] = "grpc",
            [optionsKey] = new Hashtable { ["grpc-service-name"] = "example" }
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("authority"));
    }

    private static TestCaseData Invalid(string network, string field, object? value, string expectedPath, Hashtable? extra = null)
    {
        var fields = extra ?? new Hashtable();
        fields["network"] = network;
        fields[field] = value;
        return new TestCaseData(fields, expectedPath).SetName($"SemanticGuard_{network}_{field}_{expectedPath}_{value}");
    }

    private static ProxyOutbound Convert(string protocol, Hashtable fields)
    {
        IProxyConverter converter = protocol switch
        {
            "vless" => new VlessConverter(),
            "trojan" => new TrojanConverter(),
            "hysteria2" => new Hysteria2Converter(),
            _ => new AnyTlsConverter()
        };
        NodeConversionResult result = converter.Convert(Node(protocol, fields));
        Assert.That(result, Is.TypeOf<ConvertedNode>(), result is InvalidNode invalid ? invalid.ErrorMessage : null);
        return ((ConvertedNode)result).Outbound;
    }

    private static ClashProxyNode Node(string protocol, Hashtable fields)
    {
        var values = new Hashtable
        {
            ["name"] = "test",
            ["type"] = protocol,
            ["server"] = "node.example.com",
            ["port"] = 443,
            ["tls"] = true,
            [protocol == "vless" ? "uuid" : "password"] = protocol == "vless"
                ? "00000000-0000-4000-8000-000000000001" : "test-only"
        };
        foreach (DictionaryEntry field in fields) values[field.Key] = field.Value;
        return new ClashProxyNode(values);
    }

    private static JsonElement Serialize(Outbound outbound) => JsonSerializer.SerializeToElement<Outbound>(outbound,
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
}
