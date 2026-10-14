using System.Collections;
using BoxForge.Converters;
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
            if (expected == "30s")
                Assert.That(json, Does.Not.Contain("\"idle_session_timeout\""));
            else
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
            if (expected == "xudp")
                Assert.That(json, Does.Not.Contain("\"packet_encoding\""));
            else
                Assert.That(json, Does.Contain("\"packet_encoding\": \"packetaddr\""));
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
