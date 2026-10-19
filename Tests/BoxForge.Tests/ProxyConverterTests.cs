using System.Collections;
using System.Globalization;
using BoxForge.Converters;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Services;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProxyConverterTests
{
    private const string RealityPublicKey =
        "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";

    [TestCase("trojan", "port", "443")]
    [TestCase("anytls", "min-idle-session", "1")]
    [TestCase("ss", "udp-over-tcp-version", "1")]
    [TestCase("hysteria2", "ports", "443")]
    [TestCase("hysteria2", "obfs-min-packet-size", "512")]
    [TestCase("hysteria2", "obfs-max-packet-size", "1200")]
    public void IntegerFieldsUseInvariantSigns(string protocol, string field, string number)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = "^";
        CultureInfo.CurrentCulture = culture;
        try
        {
            IProxyConverter converter = protocol switch
            {
                "trojan" => new TrojanConverter(),
                "anytls" => new AnyTlsConverter(),
                "ss" => new ShadowsocksConverter(),
                _ => new Hysteria2Converter()
            };
            var fields = new Hashtable { ["password"] = "test-only" };
            if (protocol == "ss")
            {
                fields["cipher"] = "aes-128-gcm";
                fields["udp-over-tcp"] = true;
            }
            if (field.StartsWith("obfs-", StringComparison.Ordinal))
            {
                fields["obfs"] = "gecko";
                fields["obfs-password"] = "test-only";
            }

            fields[field] = "+" + number;
            Assert.That(converter.Convert(CreateNode(protocol, fields)), Is.TypeOf<ConvertedNode>());
            fields[field] = "^" + number;
            Assert.That(converter.Convert(CreateNode(protocol, fields)), Is.TypeOf<InvalidNode>());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCase("idle-session-timeout", "30", "30s")]
    [TestCase("idle_session_timeout", "45s", "45s")]
    [TestCase("idle-timeout", "1500ms", "1500ms")]
    [TestCase("idle_timeout", "1m30s", "1m30s")]
    public void AnyTlsMapsTimeoutToOfficialField(
        string inputKey,
        string inputValue,
        string expected)
    {
        AnyTlsOutbound outbound = Convert<AnyTlsOutbound>(
            new AnyTlsConverter(),
            CreateNode("anytls", new Hashtable
            {
                ["password"] = "secret",
                [inputKey] = inputValue
            }));

        string json = SerializeOutbound(outbound);

        Assert.Multiple(() =>
        {
            Assert.That(outbound.IdleSessionTimeout, Is.EqualTo(expected));
            Assert.That(json, Does.Contain($"\"idle_session_timeout\": \"{expected}\""));
            Assert.That(json, Does.Not.Contain("\"idle_timeout\""));
        });
    }

    [TestCase("packet-encoding", "packetaddr", "packetaddr")]
    [TestCase("packet_encoding", "XUDP", "xudp")]
    [TestCase("packet-encoding", "", "xudp")]
    [TestCase("packet-encoding", "packet", "packetaddr")]
    [TestCase("packet-encoding", "PACKETADDR", "xudp")]
    [TestCase("packet-encoding", " packetaddr ", "xudp")]
    [TestCase("packet-encoding", "unknown", "xudp")]
    public void VlessMapsPacketEncodingFromSource(
        string inputKey,
        string inputValue,
        string expected)
    {
        VlessOutbound outbound = Convert<VlessOutbound>(
            new VlessConverter(),
            CreateNode("vless", new Hashtable
            {
                ["uuid"] = "00000000-0000-4000-8000-000000000001",
                [inputKey] = inputValue
            }));

        string json = SerializeOutbound(outbound);
        Assert.Multiple(() =>
        {
            Assert.That(outbound.PacketEncoding, Is.EqualTo(expected));
            Assert.That(json, Does.Contain($"\"packet_encoding\": \"{expected}\""));
        });
    }

    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase("short-ignored", null)]
    [TestCase("xtls-rprx-vision", "xtls-rprx-vision")]
    [TestCase("xtls-rprx-vision-udp443", "xtls-rprx-vision")]
    [TestCase("xtls-rprx-vision任意后缀", "xtls-rprx-vision")]
    public void VlessMapsEffectiveSourceFlow(string? flow, string? expected)
    {
        VlessOutbound outbound = Convert<VlessOutbound>(
            new VlessConverter(), CreateNode("vless", new Hashtable
            {
                ["uuid"] = "00000000-0000-4000-8000-000000000001",
                ["flow"] = flow
            }));
        Assert.That(outbound.Flow, Is.EqualTo(expected));
    }

    [TestCase("unsupported-flow")]
    [TestCase("XTLS-RPRX-VISION")]
    [TestCase("不支持的流控制协议")]
    public void VlessRejectsUnsupportedSourceFlow(string flow)
    {
        NodeConversionResult result = new VlessConverter().Convert(CreateNode("vless", new Hashtable
        {
            ["uuid"] = "00000000-0000-4000-8000-000000000001",
            ["flow"] = flow
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("flow"));
    }

    [TestCase("ss")]
    [TestCase("trojan")]
    [TestCase("hysteria2")]
    [TestCase("anytls")]
    public void PasswordPreservesWhitespaceThroughConversionAndSerialization(string protocol)
    {
        IProxyConverter converter = protocol switch
        {
            "ss" => new ShadowsocksConverter(),
            "trojan" => new TrojanConverter(),
            "hysteria2" => new Hysteria2Converter(),
            _ => new AnyTlsConverter()
        };
        foreach (string password in new[] { " test-only ", "  " })
        {
            var extra = new Hashtable { ["password"] = password };
            if (protocol == "ss")
                extra["cipher"] = "aes-128-gcm";
            ProxyOutbound outbound = Convert<ProxyOutbound>(converter, CreateNode(protocol, extra));
            using var json = System.Text.Json.JsonDocument.Parse(SerializeOutbound(outbound));
            Assert.That(json.RootElement.GetProperty("outbounds")[0].GetProperty("password").GetString(),
                Is.EqualTo(password), protocol);
        }
    }

    [Test]
    public void VlessKeepsXudpDefaultWhenSourceOmitsPacketEncoding()
    {
        VlessOutbound outbound = Convert<VlessOutbound>(
            new VlessConverter(),
            CreateNode("vless", new Hashtable
            {
                ["uuid"] = "00000000-0000-4000-8000-000000000001"
            }));

        Assert.That(outbound.PacketEncoding, Is.EqualTo("xudp"));
    }

    [TestCase(null, "00", "Reality 缺失必填字段或为空: public-key")]
    [TestCase("not-a-key", "00", "Reality public-key 必须是 32 字节的 Base64URL 公钥")]
    [TestCase(RealityPublicKey, null, "Reality 缺失必填字段: short-id")]
    [TestCase(RealityPublicKey, "abc", "Reality short-id 必须是 0 到 8 字节的偶数位十六进制字符串")]
    [TestCase(RealityPublicKey, "001122334455667788", "Reality short-id 必须是 0 到 8 字节的偶数位十六进制字符串")]
    public void RealityRejectsInvalidRequiredFields(
        string? publicKey,
        string? shortId,
        string expectedError)
    {
        var reality = new Hashtable();
        if (publicKey != null)
        {
            reality["public-key"] = publicKey;
        }
        if (shortId != null)
        {
            reality["short-id"] = shortId;
        }

        NodeConversionResult result = new VlessConverter().Convert(
            CreateNode("vless", new Hashtable
            {
                ["uuid"] = "00000000-0000-4000-8000-000000000001",
                ["tls"] = true,
                ["client-fingerprint"] = "chrome",
                ["reality-opts"] = reality
            }));

        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain(expectedError));
    }

    [TestCase("")]
    [TestCase("00")]
    [TestCase("0011223344556677")]
    public void RealityAcceptsValidShortIds(string shortId)
    {
        VlessOutbound outbound = Convert<VlessOutbound>(
            new VlessConverter(),
            CreateNode("vless", new Hashtable
            {
                ["uuid"] = "00000000-0000-4000-8000-000000000001",
                ["tls"] = true,
                ["client-fingerprint"] = "chrome",
                ["reality-opts"] = new Hashtable
                {
                    ["public-key"] = RealityPublicKey,
                    ["short-id"] = shortId
                }
            }));

        Assert.That(outbound.Tls?.Reality?.ShortId, Is.EqualTo(shortId));
    }

    [TestCase("chrome")]
    [TestCase("firefox")]
    [TestCase("safari")]
    [TestCase("ios")]
    [TestCase("android")]
    [TestCase("edge")]
    [TestCase("360")]
    [TestCase("qq")]
    [TestCase("randomized")]
    public void TlsPreservesSupportedFingerprintNames(string fingerprint)
    {
        TrojanOutbound outbound = Convert<TrojanOutbound>(new TrojanConverter(), CreateNode("trojan", new Hashtable
        {
            ["password"] = "test-only",
            ["client-fingerprint"] = fingerprint
        }));
        Assert.That(outbound.Tls!.Utls!.Fingerprint, Is.EqualTo(fingerprint));
        Assert.That(outbound.Tls.Utls.Enabled, Is.True);
    }

    [TestCase("client-fingerprint", "none")]
    [TestCase("client_fingerprint", "none")]
    [TestCase("client-fingerprint", "")]
    public void NativeTlsFingerprintDoesNotGenerateUtls(string field, string fingerprint)
    {
        TrojanOutbound outbound = Convert<TrojanOutbound>(new TrojanConverter(), CreateNode("trojan", new Hashtable
        {
            ["password"] = "test-only",
            [field] = fingerprint
        }));
        Assert.That(outbound.Tls!.Utls, Is.Null);
        Assert.That(SerializeOutbound(outbound), Does.Not.Contain("\"utls\""));
    }

    [TestCase("chrome120")]
    [TestCase("firefox120")]
    [TestCase("safari16")]
    [TestCase("chrome_psk")]
    [TestCase("chrome_psk_shuffle")]
    [TestCase("chrome_padding_psk_shuffle")]
    [TestCase("chrome_pq")]
    [TestCase("chrome_pq_psk")]
    [TestCase("random")]
    [TestCase("unknown")]
    [TestCase("CHROME")]
    public void TlsRejectsFingerprintsWithoutAnEquivalentTargetProfile(string fingerprint)
    {
        NodeConversionResult result = new TrojanConverter().Convert(CreateNode("trojan", new Hashtable
        {
            ["password"] = "test-only",
            ["client-fingerprint"] = fingerprint
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("client-fingerprint"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("none")]
    public void RealityRequiresAnExplicitEffectiveUtlsFingerprint(string? fingerprint)
    {
        NodeConversionResult result = new VlessConverter().Convert(CreateNode("vless", new Hashtable
        {
            ["uuid"] = "00000000-0000-4000-8000-000000000001",
            ["tls"] = true,
            ["client-fingerprint"] = fingerprint,
            ["reality-opts"] = new Hashtable { ["public-key"] = RealityPublicKey, ["short-id"] = "00" }
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("client-fingerprint"));
    }

    [TestCase(null)]
    [TestCase(false)]
    public void VlessRealityDoesNotImplicitlyEnableTls(bool? tls)
    {
        var fields = new Hashtable
        {
            ["uuid"] = "00000000-0000-4000-8000-000000000001",
            ["client-fingerprint"] = "chrome",
            ["reality-opts"] = new Hashtable { ["public-key"] = RealityPublicKey, ["short-id"] = "00" }
        };
        if (tls.HasValue) fields["tls"] = tls.Value;
        NodeConversionResult result = new VlessConverter().Convert(CreateNode("vless", fields));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("显式启用 tls"));
    }

    [TestCase("invalid")]
    [TestCase("0")]
    [TestCase("0s")]
    public void AnyTlsRejectsInvalidTimeout(string timeout)
    {
        NodeConversionResult result = new AnyTlsConverter().Convert(
            CreateNode("anytls", new Hashtable
            {
                ["password"] = "secret",
                ["idle-session-timeout"] = timeout
            }));

        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(
            ((InvalidNode)result).ErrorMessage,
            Does.Contain("AnyTLS").And.Contain("idle-session-timeout"));
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    public void NativeTcpAllowsMissingEmptyAndNormalizedNetwork(string protocol)
    {
        IProxyConverter converter = protocol == "vless" ? new VlessConverter() : new TrojanConverter();
        foreach (string? network in new string?[] { null, "", " ", "tcp", " TCP " })
        {
            var fields = new Hashtable
            {
                [protocol == "vless" ? "uuid" : "password"] = protocol == "vless"
                    ? "00000000-0000-4000-8000-000000000001" : "test-only",
                ["tls"] = true
            };
            if (network is not null) fields["network"] = network;
            ProxyOutbound outbound = Convert<ProxyOutbound>(converter, CreateNode(protocol, fields));
            Assert.That(SerializeOutbound(outbound), Does.Not.Contain("\"transport\""));
        }
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    [TestCase("anytls")]
    [TestCase("hysteria2")]
    public void SharedTlsAlpnPreservesOrderDuplicatesAndAbsence(string protocol)
    {
        IProxyConverter converter = protocol switch
        {
            "vless" => new VlessConverter(),
            "trojan" => new TrojanConverter(),
            "hysteria2" => new Hysteria2Converter(),
            _ => new AnyTlsConverter()
        };
        var fields = new Hashtable
        {
            [protocol == "vless" ? "uuid" : "password"] = protocol == "vless"
                ? "00000000-0000-4000-8000-000000000001" : "test-only",
            ["tls"] = true
        };
        ProxyOutbound absent = Convert<ProxyOutbound>(converter, CreateNode(protocol, fields));
        fields["alpn"] = new[] { "h2", "http/1.1", "h2" };
        ProxyOutbound present = Convert<ProxyOutbound>(converter, CreateNode(protocol, fields));
        Assert.That(GetTls(absent)!.Alpn, Is.Null);
        Assert.That(GetTls(present)!.Alpn, Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
        foreach (object malformed in new object[] { "h2", new object[] { 1 } })
        {
            fields["alpn"] = malformed;
            NodeConversionResult result = converter.Convert(CreateNode(protocol, fields));
            Assert.That(result, Is.TypeOf<InvalidNode>());
            Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("alpn"));
        }
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    public void TuningPreservesNativeRealityTlsAndExplicitConnectionSettings(string protocol)
    {
        IProxyConverter converter = protocol == "vless" ? new VlessConverter() : new TrojanConverter();
        ProxyOutbound source = Convert<ProxyOutbound>(converter, CreateNode(protocol, new Hashtable
        {
            [protocol == "vless" ? "uuid" : "password"] = protocol == "vless"
                ? "00000000-0000-4000-8000-000000000001" : "test-only",
            ["tls"] = true,
            ["alpn"] = new[] { "h2" },
            ["client-fingerprint"] = "chrome",
            ["reality-opts"] = new Hashtable { ["public-key"] = RealityPublicKey, ["short-id"] = "00" }
        })) with
        { ConnectTimeout = "9s", TcpKeepAlive = "2m", TcpKeepAliveInterval = "15s" };
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
        {
            ProxyOutbound tuned = OutboundTuningPolicy.Apply(source, platform);
            OutboundTls tls = GetTls(tuned)!;
            Assert.That(tls, Is.EqualTo(GetTls(source)));
            Assert.That(tls.Alpn, Is.EqualTo(new[] { "h2" }));
            Assert.That(tls.Utls!.Fingerprint, Is.EqualTo("chrome"));
            Assert.That(tls.Reality!.PublicKey, Is.EqualTo(RealityPublicKey));
            Assert.That(tuned.ConnectTimeout, Is.EqualTo("9s"));
            Assert.That(tuned.TcpKeepAlive, Is.EqualTo("2m"));
            Assert.That(tuned.TcpKeepAliveInterval, Is.EqualTo("15s"));
        }
    }

    private static OutboundTls? GetTls(ProxyOutbound outbound) => outbound switch
    {
        VlessOutbound vless => vless.Tls,
        TrojanOutbound trojan => trojan.Tls,
        Hysteria2Outbound hysteria2 => hysteria2.Tls,
        AnyTlsOutbound anyTls => anyTls.Tls,
        _ => throw new AssertionException("Expected a TLS-capable outbound.")
    };

    private static ClashProxyNode CreateNode(string type, Hashtable extra)
    {
        var values = new Hashtable
        {
            ["name"] = "test-node",
            ["type"] = type,
            ["server"] = "node.example.com",
            ["port"] = 443
        };
        foreach (DictionaryEntry entry in extra)
        {
            values[entry.Key] = entry.Value;
        }

        return new ClashProxyNode(values);
    }

    private static TOutbound Convert<TOutbound>(
        IProxyConverter converter,
        ClashProxyNode node)
        where TOutbound : ProxyOutbound
    {
        NodeConversionResult result = converter.Convert(node);
        Assert.That(result, Is.TypeOf<ConvertedNode>());
        return (TOutbound)((ConvertedNode)result).Outbound;
    }

    private static string SerializeOutbound(Outbound outbound) =>
        new ConfigSerializer().Serialize(new SingboxConfig
        {
            Outbounds = [outbound]
        });
}
