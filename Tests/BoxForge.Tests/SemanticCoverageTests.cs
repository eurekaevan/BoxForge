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
            ["plugin-opts"] = new Hashtable
            {
                ["mode"] = "websocket",
                ["mux"] = false,
                ["host"] = "example.com"
            }
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
                    ["name"] = "ss-plugin", ["plugin"] = "v2ray-plugin",
                    ["plugin-opts"] = new Hashtable { ["mode"] = "websocket", ["mux"] = false }
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

    [TestCase("vless", "network", "xhttp")]
    [TestCase("vless", "ws-opts", "ignored")]
    [TestCase("trojan", "network", "unknown")]
    [TestCase("trojan", "grpc-opts", "ignored")]
    [TestCase("hysteria2", "udp-mtu", 1400)]
    [TestCase("hysteria2", "realm-opts", "unsupported")]
    [TestCase("anytls", "disable-reuse", true)]
    [TestCase("ss", "network", "tcp")]
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

    [TestCase("udp-over-tcp-version", 3, "udp-over-tcp-version")]
    [TestCase("plugin", "obfs-local", "SIP003")]
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
            Does.Contain("plugin-opts.mux"));
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
                    ["network"] = "xhttp"
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

    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    public void ShadowsocksUotPreservesSourceVersion(int sourceVersion, int targetVersion)
    {
        var result = (ConvertedNode)new ShadowsocksConverter().Convert(CreateNode("ss", new Hashtable
        {
            ["udp-over-tcp"] = true,
            ["udp-over-tcp-version"] = sourceVersion
        }));
        var outbound = (ShadowsocksOutbound)result.Outbound;
        Assert.That(outbound.UdpOverTcp, Is.EqualTo(new UdpOverTcpOptions
        {
            Enabled = true,
            Version = targetVersion
        }));
        Assert.That(Serialize(outbound), Does.Contain("\"version\": " + targetVersion));
    }

    [Test]
    public void ShadowsocksUotDefaultIsLegacyAndDisabledIsOmitted()
    {
        var enabled = (ConvertedNode)new ShadowsocksConverter().Convert(CreateNode("ss",
            new Hashtable { ["udp_over_tcp"] = true }));
        var disabled = (ConvertedNode)new ShadowsocksConverter().Convert(CreateNode("ss",
            new Hashtable { ["udp_over_tcp"] = false }));
        Assert.That(((ShadowsocksOutbound)enabled.Outbound).UdpOverTcp!.Version, Is.EqualTo(1));
        Assert.That(Serialize(disabled.Outbound), Does.Not.Contain("udp_over_tcp"));
    }

    [TestCase("invalid")]
    [TestCase("1.5")]
    [TestCase("2147483648")]
    public void ShadowsocksMalformedUotVersionDoesNotFallBack(string value)
    {
        var result = new ShadowsocksConverter().Convert(CreateNode("ss",
            new Hashtable { ["udp-over-tcp-version"] = value }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
    }

    [TestCase("http")]
    [TestCase("tls")]
    public void ShadowsocksObfsMapsPluginNameAndSourceHostDefault(string mode)
    {
        var result = (ConvertedNode)new ShadowsocksConverter().Convert(CreateNode("ss", new Hashtable
        {
            ["plugin"] = "obfs",
            ["plugin-opts"] = new Hashtable { ["mode"] = mode }
        }));
        var outbound = (ShadowsocksOutbound)result.Outbound;
        Assert.That(outbound.Plugin, Is.EqualTo("obfs-local"));
        Assert.That(outbound.PluginOpts, Is.EqualTo($"obfs={mode};obfs-host=bing.com"));
    }

    [Test]
    public void ShadowsocksPluginUnknownNestedFieldReportsFullPath()
    {
        var result = (InvalidNode)new ShadowsocksConverter().Convert(CreateNode("ss", new Hashtable
        {
            ["plugin"] = "obfs",
            ["plugin-opts"] = new Hashtable { ["mode"] = "tls", ["future"] = true }
        }));
        Assert.That(result.ErrorMessage, Does.Contain("plugin-opts.future"));
    }

    [Test]
    public void ShadowsocksPluginUsesSip003EscapingWithoutFlatteningObjects()
    {
        var result = (ConvertedNode)new ShadowsocksConverter().Convert(CreateNode("ss", new Hashtable
        {
            ["plugin"] = "v2ray-plugin",
            ["plugin_opts"] = new Hashtable
            {
                ["mode"] = "websocket",
                ["mux"] = false,
                ["path"] = "/a;b=c\\d"
            }
        }));
        Assert.That(((ShadowsocksOutbound)result.Outbound).PluginOpts,
            Is.EqualTo("mode=websocket;host=bing.com;path=/a\\;b\\=c\\\\d;mux=0"));
    }

    [Test]
    public void ShadowsocksUotAliasConflictRejectsWholeNode()
    {
        var result = (InvalidNode)new ShadowsocksConverter().Convert(CreateNode("ss", new Hashtable
        {
            ["udp-over-tcp"] = true,
            ["udp_over_tcp"] = false
        }));
        Assert.That(result.ErrorMessage, Does.Contain("同时指定同一语义"));
    }

    [TestCase("100", 100)]
    [TestCase("100 Mbps", 100)]
    [TestCase("100000 Kbps", 100)]
    [TestCase("1 Gbps", 1000)]
    [TestCase("10 MBps", 80)]
    [TestCase("125000 Bps", 1)]
    public void Hysteria2BandwidthUsesExactUnits(string value, int expected)
    {
        var source = ConvertHysteria2(new Hashtable { ["up"] = value, ["down"] = value });
        var tuned = (Hysteria2Outbound)OutboundTuningPolicy.Apply(source, TargetPlatform.Linux);
        Assert.That(tuned.UpMbps, Is.EqualTo(expected));
        Assert.That(tuned.DownMbps, Is.EqualTo(expected));
        Assert.That(tuned.BbrProfile, Is.Null, "Bandwidth must not acquire BBR tuning");
    }

    [TestCase("100 Kbps")]
    [TestCase("1.5 Mbps")]
    [TestCase("-1")]
    [TestCase("18446744073709551615 Tbps")]
    [TestCase("100 Mbps\n")]
    public void Hysteria2InexactOrInvalidBandwidthFails(string value)
    {
        var result = new Hysteria2Converter().Convert(CreateNode("hysteria2", new Hashtable { ["up"] = value }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("up"));
    }

    [TestCase("20", "20s", null)]
    [TestCase("15-30", "15s", "30s")]
    [TestCase("2-3", "5s", "5s")]
    [TestCase("0", "30s", null)]
    [TestCase("30-15", "15s", "30s")]
    [TestCase("[15 - 30]", "15s", "30s")]
    public void Hysteria2HopIntervalPreservesFixedAndRangeSemantics(string value, string min, string? max)
    {
        var source = ConvertHysteria2(new Hashtable { ["ports"] = "4000-4001", ["hop-interval"] = value });
        var tuned = (Hysteria2Outbound)OutboundTuningPolicy.Apply(source, TargetPlatform.Android);
        Assert.That(tuned.HopInterval, Is.EqualTo(min));
        Assert.That(tuned.HopIntervalMax, Is.EqualTo(max));
    }

    [Test]
    public void Hysteria2AbsentSourceRetainsNamedTuning()
    {
        var tuned = (Hysteria2Outbound)OutboundTuningPolicy.Apply(ConvertHysteria2(new Hashtable()), TargetPlatform.Linux);
        Assert.That(tuned.HopInterval, Is.EqualTo("30s"));
        Assert.That(tuned.HopIntervalMax, Is.EqualTo("60s"));
        Assert.That(tuned.BbrProfile, Is.EqualTo("standard"));
    }

    [TestCase("standard")]
    [TestCase("conservative")]
    [TestCase("aggressive")]
    public void Hysteria2ExplicitBbrWinsOverTuning(string profile)
    {
        var tuned = (Hysteria2Outbound)OutboundTuningPolicy.Apply(
            ConvertHysteria2(new Hashtable { ["bbr_profile"] = profile }), TargetPlatform.Linux);
        Assert.That(tuned.BbrProfile, Is.EqualTo(profile));
    }

    [TestCase("20s")]
    [TestCase("1.5")]
    [TestCase("9223372037")]
    public void Hysteria2InvalidHopIntervalRejectsWholeNode(string interval)
    {
        var result = new Hysteria2Converter().Convert(CreateNode("hysteria2",
            new Hashtable { ["hop-interval"] = interval }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
    }

    [Test]
    public void Hysteria2ZeroGeckoBoundsUseCoreDefaults()
    {
        var source = ConvertHysteria2(new Hashtable
        {
            ["obfs"] = "gecko",
            ["obfs-password"] = "test-only",
            ["obfs-min-packet-size"] = 0,
            ["obfs-max-packet-size"] = 0
        });
        Assert.That(source.Obfs!.MinPacketSize, Is.Null);
        Assert.That(source.Obfs.MaxPacketSize, Is.Null);
    }

    [Test]
    public void Hysteria2GeckoSizesCannotBeAppliedToSalamander()
    {
        var result = new Hysteria2Converter().Convert(CreateNode("hysteria2", new Hashtable
        {
            ["obfs"] = "salamander",
            ["obfs-password"] = "test-only",
            ["obfs-min-packet-size"] = 0
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
    }

    [TestCase(512, 1200)]
    [TestCase(1, 2048)]
    [TestCase(600, 600)]
    public void Hysteria2GeckoBoundsMapExactly(int min, int max)
    {
        var outbound = ConvertHysteria2(new Hashtable
        {
            ["obfs"] = "gecko",
            ["obfs-password"] = " test-only ",
            ["obfs_min_packet_size"] = min,
            ["obfs_max_packet_size"] = max
        });
        Assert.That(outbound.Obfs!.MinPacketSize, Is.EqualTo(min));
        Assert.That(outbound.Obfs.MaxPacketSize, Is.EqualTo(max));
        Assert.That(outbound.Obfs.Password, Is.EqualTo(" test-only "));
    }

    [TestCase(1201, 1200)]
    [TestCase(512, 2049)]
    [TestCase(-1, 1200)]
    public void Hysteria2InvalidGeckoBoundsRejectWholeNode(int min, int max)
    {
        var result = new Hysteria2Converter().Convert(CreateNode("hysteria2", new Hashtable
        {
            ["obfs"] = "gecko",
            ["obfs-password"] = "test-only",
            ["obfs-min-packet-size"] = min,
            ["obfs-max-packet-size"] = max
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
    }

    private static Hysteria2Outbound ConvertHysteria2(Hashtable fields) =>
        (Hysteria2Outbound)((ConvertedNode)new Hysteria2Converter().Convert(CreateNode("hysteria2", fields))).Outbound;

    [TestCase("30", "30s")]
    [TestCase("1h30m", "1h30m")]
    [TestCase(".5s", ".5s")]
    [TestCase("250ms", "250ms")]
    [TestCase("1μs", "1μs")]
    public void AnyTlsSessionDurationsSharePositiveGoParser(string value, string expected)
    {
        var result = (ConvertedNode)new AnyTlsConverter().Convert(CreateNode("anytls", new Hashtable
        {
            ["idle-session-timeout"] = value,
            ["idle_session_check_interval"] = value
        }));
        var outbound = (AnyTlsOutbound)result.Outbound;
        Assert.That(outbound.IdleSessionTimeout, Is.EqualTo(expected));
        Assert.That(outbound.IdleSessionCheckInterval, Is.EqualTo(expected));
    }

    [TestCase("0")]
    [TestCase("-5")]
    [TestCase("0s")]
    [TestCase("0.1ns")]
    [TestCase("9223372037")]
    [TestCase("2562048h")]
    [TestCase("1s trailing")]
    public void AnyTlsInvalidDurationRejectsWholeNode(string value)
    {
        var result = new AnyTlsConverter().Convert(CreateNode("anytls", new Hashtable
        {
            ["idle-session-check-interval"] = value
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Does.Contain("idle-session-check-interval"));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(int.MaxValue)]
    public void AnyTlsMinimumIdleAndMetadataPreserveSource(int count)
    {
        var result = (ConvertedNode)new AnyTlsConverter().Convert(CreateNode("anytls", new Hashtable
        {
            ["min_idle_session"] = count,
            ["client_metadata"] = " utf8-中文=value "
        }));
        var outbound = (AnyTlsOutbound)result.Outbound;
        Assert.That(outbound.MinIdleSession, Is.EqualTo(count));
        Assert.That(outbound.ClientMetadata, Is.EqualTo(" utf8-中文=value "));
        Assert.That(Serialize(outbound), Does.Contain("min_idle_session").And.Contain("client_metadata"));
    }

    [Test]
    public void AnyTlsUnsafeMetadataIsNotStringifiedOrInjected()
    {
        foreach (object value in new object[] { 12, new Hashtable { ["client"] = "value" }, "x\nv=1", new string('中', 21827) })
        {
            var result = new AnyTlsConverter().Convert(CreateNode("anytls", new Hashtable { ["client-metadata"] = value }));
            Assert.That(result, Is.TypeOf<InvalidNode>());
        }
    }

    [TestCase(-1)]
    [TestCase("1.5")]
    [TestCase("2147483648")]
    public void AnyTlsInvalidMinimumIdleRejectsWholeNode(object count)
    {
        var result = new AnyTlsConverter().Convert(CreateNode("anytls", new Hashtable { ["min-idle-session"] = count }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
    }

    [TestCase("idle-session-check-interval", "idle_session_check_interval", 30)]
    [TestCase("min-idle-session", "min_idle_session", 1)]
    [TestCase("client-metadata", "client_metadata", "value")]
    public void AnyTlsNewAliasesCannotShadowEachOther(string field, string alias, object value)
    {
        var result = (InvalidNode)new AnyTlsConverter().Convert(CreateNode("anytls",
            new Hashtable { [field] = value, [alias] = value }));
        Assert.That(result.ErrorMessage, Does.Contain(field).And.Contain(alias));
    }

    [TestCase("idle-session-check-interval")]
    [TestCase("min-idle-session")]
    [TestCase("client-metadata")]
    public void AnyTlsNullOptionalFieldsFollowSourceDefaultSemantics(string field)
    {
        var result = new AnyTlsConverter().Convert(CreateNode("anytls", new Hashtable { [field] = null }));
        Assert.That(result, Is.TypeOf<ConvertedNode>());
        Assert.That(Serialize(((ConvertedNode)result).Outbound), Does.Not.Contain(field.Replace('-', '_')));
    }

    [Test]
    public void AnyTlsAbsentOptionalSessionFieldsAreOmitted()
    {
        var outbound = ((ConvertedNode)new AnyTlsConverter().Convert(CreateNode("anytls"))).Outbound;
        string json = Serialize(outbound);
        Assert.That(json, Does.Not.Contain("idle_session").And.Not.Contain("min_idle_session").And.Not.Contain("client_metadata"));
    }

    [Test]
    public void ShadowsocksInvalidVersionFailsStrictAndSkipsWholeNodeNonStrict()
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder([new ShadowsocksConverter()], logger);
        var config = new ClashConfig
        {
            Proxies =
        [
            CreateNode("ss", new Hashtable { ["udp-over-tcp"] = true, ["udp-over-tcp-version"] = 3 }),
            CreateNode("ss", new Hashtable { ["name"] = "safe-node" })
        ]
        };
        var error = Assert.Throws<NodeParseException>(() => builder.Build(config, strictNodeValidation: true));
        Assert.That(error!.Message, Does.Contain("udp-over-tcp-version"));
        var catalog = builder.Build(config, strictNodeValidation: false);
        Assert.That(catalog.Outbounds.Select(outbound => outbound.Tag), Is.EqualTo(new[] { "safe-node" }));
        Assert.That(logger.Messages, Has.Count.EqualTo(1));
    }

    [TestCase(null)]
    [TestCase("")]
    public void ShadowsocksEmptyPluginAndNullVersionFollowSourceDefaults(string? plugin)
    {
        var result = (ConvertedNode)new ShadowsocksConverter().Convert(CreateNode("ss", new Hashtable
        {
            ["plugin"] = plugin,
            ["udp-over-tcp"] = true,
            ["udp-over-tcp-version"] = null
        }));
        var outbound = (ShadowsocksOutbound)result.Outbound;
        Assert.That(outbound.Plugin, Is.Null);
        Assert.That(outbound.UdpOverTcp!.Version, Is.EqualTo(1));
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
