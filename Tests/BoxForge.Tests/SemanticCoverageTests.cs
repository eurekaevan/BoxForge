using System.Collections;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;
using BoxForge.Services;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SemanticCoverageTests
{
    private const string RealityPublicKey =
        "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";

    private static IEnumerable<TestCaseData> RemovedTransports()
    {
        foreach (string protocol in new[] { "vless", "trojan" })
        {
            foreach (string network in new[] { "ws", "grpc", "httpupgrade", "http", "h2", "xhttp", "unknown", " WS " })
                yield return new TestCaseData(protocol, "network", network);
            foreach (string field in new[] { "ws-opts", "grpc-opts", "http-opts", "h2-opts", "http-upgrade-opts", "httpupgrade-opts", "xhttp-opts", "smux-opts" })
                foreach (string alias in new[] { field, field.Replace('-', '_') })
                    yield return new TestCaseData(protocol, alias, new Hashtable());
        }
    }

    [TestCaseSource(nameof(RemovedTransports))]
    public void RemovedTransportsRejectBatchContainingValidNode(string protocol, string field, object value)
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        var config = new ClashConfig
        {
            Proxies = [CreateNode(protocol, new Hashtable { [field] = value }), CreateNode(protocol, new Hashtable { ["name"] = "safe" })]
        };
        NodeParseException? error = Assert.Throws<NodeParseException>(() => builder.Build(config));
        Assert.That(error!.Message, Does.Contain(field));
    }

    [Test]
    public void Hysteria2PortsPreserveUpstreamPrecedenceOverPort()
    {
        var result = ParseAndEmit(CreateNode("hysteria2",
            new Hashtable { ["ports"] = "4000-5000" }));

        Assert.That(result, Is.InstanceOf<ProxyOutbound>());
        var outbound = (Hysteria2Outbound)result;
        Assert.Multiple(() =>
        {
            Assert.That(outbound.ServerPort, Is.Null);
            Assert.That(outbound.ServerPorts, Is.EqualTo(new[] { "4000:5000" }));
        });
    }

    [TestCase("vless", "encryption", "nonempty")]
    [TestCase("vless", "fingerprint", "nonempty")]
    [TestCase("vless", "name-cert-verify", "nonempty")]
    [TestCase("trojan", "ss-opts", "unsupported")]
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
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode(protocol,
            new Hashtable { [field] = value })))!;

        Assert.That(result.Message, Does.Contain(field));
        Assert.That(result.Message, Does.Contain("test-node"));
    }

    [Test]
    public void NestedRealityUnknownFieldIsNotSilentlyDiscarded()
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode(
            "vless", new Hashtable
            {
                ["reality-opts"] = new Hashtable
                {
                    ["public-key"] = RealityPublicKey,
                    ["short-id"] = "00",
                    ["future-key-exchange"] = true
                }
            })))!;

        Assert.That(result.Message,
            Does.Contain("reality-opts.future-key-exchange"));
    }

    [Test]
    public void ConflictingAliasesCannotSilentlyShadowEachOther()
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode(
            "vless", new Hashtable
            {
                ["packet-encoding"] = "xudp",
                ["packet_encoding"] = "packetaddr"
            })))!;

        Assert.That(result.Message,
            Does.Contain("同时指定同一语义"));
    }

    [TestCase("udp-over-tcp-version", 3, "udp-over-tcp-version")]
    [TestCase("plugin", "obfs-local", "仅支持 obfs")]
    [TestCase("plugin-opts", "invalid", "没有插件")]
    public void UnsafeExistingShadowsocksMappingsFailFast(
        string field,
        object value,
        string reason)
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(
            CreateNode("ss", new Hashtable { [field] = value })))!;

        Assert.That(result.Message, Does.Contain(reason));
    }

    [TestCase(null)]
    [TestCase("plugin-opts")]
    [TestCase("plugin_opts")]
    public void RemovedV2RayPluginRejectsWholeNode(string? optionsKey)
    {
        var fields = new Hashtable { ["plugin"] = "v2ray-plugin" };
        if (optionsKey is not null)
            fields[optionsKey] = new Hashtable { ["mode"] = "websocket", ["mux"] = false };
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        var config = new ClashConfig { Proxies = [CreateNode("ss", fields), CreateNode("ss", new Hashtable { ["name"] = "safe" })] };
        NodeParseException? error = Assert.Throws<NodeParseException>(() => builder.Build(config));
        Assert.That(error!.Message, Does.Contain("plugin"));
    }

    [Test]
    public void StrictModeReportsProtocolNodeAndUnknownField()
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        ClashConfig config = new()
        {
            Proxies = [CreateNode("vless", new Hashtable
            {
                ["future-semantic-option"] = true
            })]
        };

        NodeParseException? exception = Assert.Throws<NodeParseException>(() =>
            builder.Build(config));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("VLESS"));
            Assert.That(exception.Message, Does.Contain("test-node"));
            Assert.That(exception.Message, Does.Contain("future-semantic-option"));
        });
    }

    [Test]
    public void UnsafeNodeRejectsBatchContainingValidNode()
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
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

        var error = Assert.Throws<NodeParseException>(() => builder.Build(config));
        Assert.That(error!.Message, Does.Contain("test-node").And.Contain("network"));
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

        Assert.That(ParseAndEmit(node), Is.InstanceOf<ProxyOutbound>());
    }

    [TestCase("udp", false)]
    [TestCase("tfo", true)]
    [TestCase("ip-version", "dual")]
    [TestCase("sni", "example.com")]
    public void CommonExplicitConnectionSettingsCannotBeSilentlyIgnored(
        string field,
        object value)
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode(
            "vless", new Hashtable { [field] = value })))!;

        Assert.That(result.Message, Does.Contain(field));
    }

    [Test]
    public void Hysteria2RejectsExplicitUtlsFingerprint()
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(
            CreateNode("hysteria2", new Hashtable
            {
                ["client-fingerprint"] = "chrome"
            })))!;

        Assert.That(result.Message,
            Does.Contain("client-fingerprint"));
    }

    [TestCase("server")]
    [TestCase("uuid")]
    [TestCase("sni")]
    public void MappedScalarFieldsRejectStructuredValues(string field)
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode(
            "vless", new Hashtable
            {
                ["tls"] = true,
                [field] = new List<string> { "invalid", "shape" }
            })))!;

        Assert.That(result.Message,
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
        Hysteria2Node source = ParseHysteria2(new Hashtable
        {
            ["hop-interval"] = "20-40",
            ["bbr-profile"] = "conservative"
        }) with
        { Connection = new ConnectionSettings("9s", "2m", "12s") };
        var linux = (Hysteria2Outbound)SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(
            source, BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)));
        var android = (Hysteria2Outbound)SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(
            source with { Connection = source.Connection with { TcpKeepAlive = null } },
            BoxForgeProfile.Default.GetPlatform(TargetPlatform.Android)));

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
            Assert.That(source.HopInterval!.MaximumSeconds, Is.EqualTo(40));
        });
    }

    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    public void ShadowsocksUotPreservesSourceVersion(int sourceVersion, int targetVersion)
    {
        var result = ParseAndEmit(CreateNode("ss", new Hashtable
        {
            ["udp-over-tcp"] = true,
            ["udp-over-tcp-version"] = sourceVersion
        }));
        var outbound = (ShadowsocksOutbound)result;
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
        var enabled = ParseAndEmit(CreateNode("ss",
            new Hashtable { ["udp_over_tcp"] = true }));
        var disabled = ParseAndEmit(CreateNode("ss",
            new Hashtable { ["udp_over_tcp"] = false }));
        Assert.That(((ShadowsocksOutbound)enabled).UdpOverTcp!.Version, Is.EqualTo(1));
        Assert.That(Serialize(disabled), Does.Not.Contain("udp_over_tcp"));
    }

    [TestCase("invalid")]
    [TestCase("1.5")]
    [TestCase("2147483648")]
    public void ShadowsocksMalformedUotVersionDoesNotFallBack(string value)
    {
        Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("ss",
            new Hashtable { ["udp-over-tcp-version"] = value })));
    }

    [TestCase("http")]
    [TestCase("tls")]
    public void ShadowsocksObfsMapsPluginNameAndSourceHostDefault(string mode)
    {
        var result = ParseAndEmit(CreateNode("ss", new Hashtable
        {
            ["plugin"] = "obfs",
            ["plugin-opts"] = new Hashtable { ["mode"] = mode }
        }));
        var outbound = (ShadowsocksOutbound)result;
        Assert.That(outbound.Plugin, Is.EqualTo("obfs-local"));
        Assert.That(outbound.PluginOpts, Is.EqualTo($"obfs={mode};obfs-host=bing.com"));
    }

    [Test]
    public void ShadowsocksPluginUnknownNestedFieldReportsFullPath()
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("ss", new Hashtable
        {
            ["plugin"] = "obfs",
            ["plugin-opts"] = new Hashtable { ["mode"] = "tls", ["future"] = true }
        })))!;
        Assert.That(result.Message, Does.Contain("plugin-opts.future"));
    }

    [Test]
    public void ShadowsocksPluginUsesSip003EscapingWithoutFlatteningObjects()
    {
        var result = ParseAndEmit(CreateNode("ss", new Hashtable
        {
            ["plugin"] = "obfs",
            ["plugin_opts"] = new Hashtable { ["mode"] = "http", ["host"] = "a;b=c\\d" }
        }));
        Assert.That(((ShadowsocksOutbound)result).PluginOpts,
            Is.EqualTo("obfs=http;obfs-host=a\\;b\\=c\\\\d"));
    }

    [Test]
    public void ShadowsocksUotAliasConflictRejectsWholeNode()
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("ss", new Hashtable
        {
            ["udp-over-tcp"] = true,
            ["udp_over_tcp"] = false
        })))!;
        Assert.That(result.Message, Does.Contain("同时指定同一语义"));
    }

    [TestCase("100", 100)]
    [TestCase("100 Mbps", 100)]
    [TestCase("100000 Kbps", 100)]
    [TestCase("1 Gbps", 1000)]
    [TestCase("10 MBps", 80)]
    [TestCase("125000 Bps", 1)]
    public void Hysteria2BandwidthUsesExactUnits(string value, int expected)
    {
        var source = ParseHysteria2(new Hashtable { ["up"] = value, ["down"] = value });
        var tuned = (Hysteria2Outbound)SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(source, BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)));
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
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("hysteria2", new Hashtable { ["up"] = value })))!;

        Assert.That(result.Message, Does.Contain("up"));
    }

    [TestCase("20", "20s", null)]
    [TestCase("15-30", "15s", "30s")]
    [TestCase("2-3", "5s", "5s")]
    [TestCase("0", "30s", null)]
    [TestCase("30-15", "15s", "30s")]
    [TestCase("[15 - 30]", "15s", "30s")]
    public void Hysteria2HopIntervalPreservesFixedAndRangeSemantics(string value, string min, string? max)
    {
        var source = ParseHysteria2(new Hashtable { ["ports"] = "4000-4001", ["hop-interval"] = value });
        var tuned = (Hysteria2Outbound)SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(source, BoxForgeProfile.Default.GetPlatform(TargetPlatform.Android)));
        Assert.That(tuned.HopInterval, Is.EqualTo(min));
        Assert.That(tuned.HopIntervalMax, Is.EqualTo(max));
    }

    [Test]
    public void Hysteria2AbsentSourceRetainsNamedTuning()
    {
        var tuned = (Hysteria2Outbound)SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(ParseHysteria2(new Hashtable()), BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)));
        Assert.That(tuned.HopInterval, Is.EqualTo("30s"));
        Assert.That(tuned.HopIntervalMax, Is.EqualTo("60s"));
        Assert.That(tuned.BbrProfile, Is.EqualTo("standard"));
    }

    [TestCase("standard")]
    [TestCase("conservative")]
    [TestCase("aggressive")]
    public void Hysteria2ExplicitBbrWinsOverTuning(string profile)
    {
        var tuned = (Hysteria2Outbound)SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(
            ParseHysteria2(new Hashtable { ["bbr_profile"] = profile }), BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)));
        Assert.That(tuned.BbrProfile, Is.EqualTo(profile));
    }

    [TestCase("20s")]
    [TestCase("1.5")]
    [TestCase("9223372037")]
    public void Hysteria2InvalidHopIntervalRejectsWholeNode(string interval)
    {
        Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("hysteria2",
            new Hashtable { ["hop-interval"] = interval })));
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
        Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("hysteria2", new Hashtable
        {
            ["obfs"] = "salamander",
            ["obfs-password"] = "test-only",
            ["obfs-min-packet-size"] = 0
        })));
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
        Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("hysteria2", new Hashtable
        {
            ["obfs"] = "gecko",
            ["obfs-password"] = "test-only",
            ["obfs-min-packet-size"] = min,
            ["obfs-max-packet-size"] = max
        })));
    }

    [TestCase("30", "30s")]
    [TestCase("1h30m", "1h30m")]
    [TestCase(".5s", ".5s")]
    [TestCase("250ms", "250ms")]
    [TestCase("1μs", "1μs")]
    public void AnyTlsSessionDurationsSharePositiveGoParser(string value, string expected)
    {
        var result = ParseAndEmit(CreateNode("anytls", new Hashtable
        {
            ["idle-session-timeout"] = value,
            ["idle_session_check_interval"] = value
        }));
        var outbound = (AnyTlsOutbound)result;
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
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("anytls", new Hashtable
        {
            ["idle-session-check-interval"] = value
        })))!;

        Assert.That(result.Message, Does.Contain("idle-session-check-interval"));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(int.MaxValue)]
    public void AnyTlsMinimumIdleAndMetadataPreserveSource(int count)
    {
        var result = ParseAndEmit(CreateNode("anytls", new Hashtable
        {
            ["min_idle_session"] = count,
            ["client_metadata"] = " utf8-中文=value "
        }));
        var outbound = (AnyTlsOutbound)result;
        Assert.That(outbound.MinIdleSession, Is.EqualTo(count));
        Assert.That(outbound.ClientMetadata, Is.EqualTo(" utf8-中文=value "));
        Assert.That(Serialize(outbound), Does.Contain("client_metadata"));
        Assert.That(Serialize(outbound), Does.Contain("\"min_idle_session\": " + count));
    }

    [Test]
    public void AnyTlsUnsafeMetadataIsNotStringifiedOrInjected()
    {
        foreach (object value in new object[] { 12, new Hashtable { ["client"] = "value" }, "x\nv=1", new string('中', 21827) })
        {
            Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("anytls", new Hashtable { ["client-metadata"] = value })));

        }
    }

    [TestCase(-1)]
    [TestCase("1.5")]
    [TestCase("2147483648")]
    public void AnyTlsInvalidMinimumIdleRejectsWholeNode(object count)
    {
        Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("anytls", new Hashtable { ["min-idle-session"] = count })));
    }

    [TestCase("idle-session-check-interval", "idle_session_check_interval", 30)]
    [TestCase("min-idle-session", "min_idle_session", 1)]
    [TestCase("client-metadata", "client_metadata", "value")]
    public void AnyTlsNewAliasesCannotShadowEachOther(string field, string alias, object value)
    {
        var result = Assert.Throws<NodeParseException>(() => ParseAndEmit(CreateNode("anytls",
            new Hashtable { [field] = value, [alias] = value })))!;
        Assert.That(result.Message, Does.Contain(field).And.Contain(alias));
    }

    [TestCase("idle-session-check-interval")]
    [TestCase("min-idle-session")]
    [TestCase("client-metadata")]
    public void AnyTlsNullOptionalFieldsFollowSourceDefaultSemantics(string field)
    {
        var result = ParseAndEmit(CreateNode("anytls", new Hashtable { [field] = null }));
        Assert.That(result, Is.InstanceOf<ProxyOutbound>());
        Assert.That(Serialize(result), Does.Not.Contain(field.Replace('-', '_')));
    }

    [Test]
    public void AnyTlsAbsentOptionalSessionFieldsAreOmitted()
    {
        var outbound = ParseAndEmit(CreateNode("anytls"));
        string json = Serialize(outbound);
        Assert.That(json, Does.Not.Contain("idle_session").And.Not.Contain("min_idle_session").And.Not.Contain("client_metadata"));
    }

    [Test]
    public void ShadowsocksInvalidVersionRejectsBatchContainingValidNode()
    {
        var builder = new NodeCatalogBuilder(BoxForgeProfile.Default);
        var config = new ClashConfig
        {
            Proxies =
        [
            CreateNode("ss", new Hashtable { ["udp-over-tcp"] = true, ["udp-over-tcp-version"] = 3 }),
            CreateNode("ss", new Hashtable { ["name"] = "safe-node" })
        ]
        };
        var error = Assert.Throws<NodeParseException>(() => builder.Build(config));
        Assert.That(error!.Message, Does.Contain("udp-over-tcp-version"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void ShadowsocksEmptyPluginAndNullVersionFollowSourceDefaults(string? plugin)
    {
        var result = ParseAndEmit(CreateNode("ss", new Hashtable
        {
            ["plugin"] = plugin,
            ["udp-over-tcp"] = true,
            ["udp-over-tcp-version"] = null
        }));
        var outbound = (ShadowsocksOutbound)result;
        Assert.That(outbound.Plugin, Is.Null);
        Assert.That(outbound.UdpOverTcp!.Version, Is.EqualTo(1));
    }

    private static Hysteria2Node ParseHysteria2(Hashtable fields) =>
        (Hysteria2Node)ProxyNodeParser.Parse(CreateNode("hysteria2", fields));

    private static Hysteria2Outbound ConvertHysteria2(Hashtable fields) =>
        (Hysteria2Outbound)ParseAndEmit(CreateNode("hysteria2", fields));

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

    private static ProxyOutbound ParseAndEmit(ClashProxyNode node) =>
        SingboxNodeEmitter.Emit(ProxyNodeParser.Parse(node));

    private static string Serialize(Outbound outbound) =>
        new ConfigSerializer().Serialize(new SingboxConfig
        {
            Outbounds = [outbound]
        });

}
