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
            Assert.That(json, Does.Contain($"\"idle_session_timeout\": \"{expected}\""));
            Assert.That(json, Does.Not.Contain("\"idle_timeout\""));
        });
    }

    [TestCase("packet-encoding", "packetaddr", "packetaddr")]
    [TestCase("packet_encoding", "XUDP", "xudp")]
    [TestCase("packet-encoding", "", "")]
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

        Assert.That(outbound.PacketEncoding, Is.EqualTo(expected));
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
                ["reality-opts"] = new Hashtable
                {
                    ["public-key"] = RealityPublicKey,
                    ["short-id"] = shortId
                }
            }));

        Assert.That(outbound.Tls?.Reality?.ShortId, Is.EqualTo(shortId));
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
            Does.Contain("AnyTLS idle-session-timeout"));
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
