using System.Collections;
using System.Globalization;
using BoxForge.Converters;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using BoxForge.Builders;
using BoxForge.Models.Intermediate;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProxyConverterTests
{
    private const string RealityPublicKey =
        "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";

    [TestCase(TargetPlatform.Android, false)]
    [TestCase(TargetPlatform.Linux, false)]
    [TestCase(TargetPlatform.Windows, false)]
    [TestCase(TargetPlatform.Android, true)]
    [TestCase(TargetPlatform.Linux, true)]
    [TestCase(TargetPlatform.Windows, true)]
    public void IrTuningMatchesLegacyOutputWithoutOverwritingSource(TargetPlatform platform, bool explicitConnection)
    {
        ProxyNode[] nodes =
        [
            ParseVless(CreateNode("vless", new Hashtable { ["uuid"] = "test-only" })),
            ParseTrojan(CreateNode("trojan", new Hashtable { ["password"] = "test-only" })),
            ParseAnyTls(CreateNode("anytls", new Hashtable { ["password"] = "test-only" })),
            ParseShadowsocks(CreateNode("ss", new Hashtable { ["password"] = "test-only", ["cipher"] = "aes-128-gcm" })),
            ParseHysteria2(CreateNode("hysteria2", new Hashtable { ["password"] = "test-only" })),
            ParseHysteria2(CreateNode("hysteria2", new Hashtable { ["password"] = "test-only", ["hop_interval"] = "20" })),
            ParseHysteria2(CreateNode("hysteria2", new Hashtable { ["password"] = "test-only", ["hop_interval"] = "15-30", ["bbr_profile"] = "conservative" })),
            ParseHysteria2(CreateNode("hysteria2", new Hashtable { ["password"] = "test-only", ["up"] = 100 })),
            ParseHysteria2(CreateNode("hysteria2", new Hashtable { ["password"] = "test-only", ["up"] = 0 }))
        ];
        var profile = BoxForgeProfile.Default.GetPlatform(platform);
        foreach (ProxyNode original in nodes)
        {
            ProxyNode source = explicitConnection
                ? original with { Connection = new ConnectionSettings("9s", "2m", "15s") } : original;
            var legacy = OutboundTuningPolicy.Apply(SingboxNodeEmitter.Emit(source), profile);
            ProxyNode tuned = OutboundTuningPolicy.Apply(source, profile);
            Assert.That(SerializeOutbound(SingboxNodeEmitter.Emit(tuned)), Is.EqualTo(SerializeOutbound(legacy)));
            Assert.That(tuned, Is.Not.SameAs(source));
            Assert.That(source.Connection.ConnectTimeout, Is.EqualTo(explicitConnection ? "9s" : null));
            if (source is Hysteria2Node hysteria2)
            {
                var effective = (Hysteria2Node)tuned;
                Assert.That(effective.HopInterval, Is.SameAs(hysteria2.HopInterval));
                Assert.That(effective.BbrProfile, Is.SameAs(hysteria2.BbrProfile));
                Assert.That(hysteria2.TuningHopInterval, Is.Null);
                Assert.That(hysteria2.TuningBbrProfile, Is.Null);
            }
            Assert.That(SerializeOutbound(SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(tuned, profile))),
                Is.EqualTo(SerializeOutbound(legacy)), "Applying tuning twice must be idempotent.");
        }
    }

    [TestCase("ss", "cipher")]
    [TestCase("shadowsocks", "method")]
    public void ShadowsocksIrKeepsCipherAliasAndRawPassword(string protocol, string cipherField)
    {
        ShadowsocksNode node = ParseShadowsocks(CreateNode(protocol, new Hashtable
        {
            [cipherField] = " aes-128-gcm ",
            ["password"] = " test-only "
        }));
        Assert.That(node.Cipher.Value, Is.EqualTo("aes-128-gcm"));
        Assert.That(node.Cipher.Field, Is.EqualTo(cipherField));
        Assert.That(node.Cipher.RuleId, Is.EqualTo(ShadowsocksSemantics.Cipher.Id));
        Assert.That(SingboxNodeEmitter.Emit(node).Password, Is.EqualTo(" test-only "));
        Assert.That(new ShadowsocksConverter().CanHandle(protocol), Is.True);
    }

    [TestCase("omitted", "omitted", 1)]
    [TestCase("null", "null", 1)]
    [TestCase("false", "2", 2)]
    [TestCase("true", "omitted", 1)]
    [TestCase("true", "null", 1)]
    [TestCase("true", "0", 1)]
    [TestCase("true", "1", 1)]
    [TestCase("true", "2", 2)]
    public void ShadowsocksIrKeepsUotOriginAndEffectiveWireVersion(string enabledInput, string versionInput, int expected)
    {
        bool? enabled = enabledInput == "true" ? true : enabledInput == "false" ? false : null;
        var fields = new Hashtable { ["cipher"] = "aes-128-gcm", ["password"] = "test-only" };
        if (enabledInput != "omitted") fields["udp_over_tcp"] = enabled;
        if (versionInput != "omitted") fields["udp_over_tcp_version"] = versionInput == "null" ? null : versionInput;
        ShadowsocksNode node = ParseShadowsocks(CreateNode("ss", fields));
        Assert.That(node.UdpOverTcpEnabled.Value, Is.EqualTo(enabled));
        Assert.That(node.UdpOverTcpEnabled.Field, Is.EqualTo(enabledInput == "omitted" ? null : "udp_over_tcp"));
        Assert.That(node.UdpOverTcpEnabled.RuleId, Is.EqualTo(ShadowsocksSemantics.UotEnabled.Id));
        Assert.That((int)node.UdpOverTcpVersion.Value, Is.EqualTo(expected));
        Assert.That(node.UdpOverTcpVersion.Field, Is.EqualTo(versionInput == "omitted" ? null : "udp_over_tcp_version"));
        Assert.That(node.UdpOverTcpVersion.RuleId, Is.EqualTo(ShadowsocksSemantics.UotVersion.Id));
        var emitted = SingboxNodeEmitter.Emit(node);
        if (enabled == true)
            Assert.That(emitted.UdpOverTcp!.Version, Is.EqualTo(expected));
        else
            Assert.That(emitted.UdpOverTcp, Is.Null);
    }

    [TestCase("plugin-opts", "http", "omitted")]
    [TestCase("plugin-opts", "tls", "null")]
    [TestCase("plugin_opts", "http", "custom")]
    [TestCase("plugin_opts", "tls", "custom")]
    public void ShadowsocksIrKeepsStructuredPluginValuesUntilEmission(string parent, string mode, string hostInput)
    {
        var options = new Hashtable { ["mode"] = mode };
        if (hostInput != "omitted") options["host"] = hostInput == "null" ? null : "a;b=c\\d";
        ShadowsocksNode node = ParseShadowsocks(CreateNode("ss", new Hashtable
        {
            ["cipher"] = "aes-128-gcm",
            ["password"] = "test-only",
            ["plugin"] = "obfs",
            [parent] = options
        }));
        ShadowsocksObfs plugin = node.Plugin.Value!;
        Assert.That(node.Plugin.Field, Is.EqualTo("plugin"));
        Assert.That(node.Plugin.RuleId, Is.EqualTo(ShadowsocksSemantics.Plugin.Id));
        Assert.That(plugin.Mode.Field, Is.EqualTo($"{parent}.mode"));
        Assert.That(plugin.Mode.RuleId, Is.EqualTo(ShadowsocksSemantics.ObfsMode.Id));
        Assert.That(plugin.Host.Value, Is.EqualTo(hostInput == "custom" ? "a;b=c\\d" : "bing.com"));
        Assert.That(plugin.Host.Field, Is.EqualTo(hostInput == "omitted" ? null : $"{parent}.host"));
        Assert.That(plugin.Host.RuleId, Is.EqualTo(ShadowsocksSemantics.ObfsHost.Id));
        var emitted = SingboxNodeEmitter.Emit(node);
        Assert.That(emitted.Plugin, Is.EqualTo("obfs-local"));
        Assert.That(emitted.PluginOpts, Is.EqualTo($"obfs={mode};obfs-host="
            + (hostInput == "custom" ? "a\\;b\\=c\\\\d" : "bing.com")));
        Assert.That(node.Plugin.Value!.Host.Value, Is.EqualTo(hostInput == "custom" ? "a;b=c\\d" : "bing.com"));
    }

    [TestCase("omitted")]
    [TestCase("null")]
    [TestCase("")]
    public void ShadowsocksIrKeepsDisabledPluginOrigin(string input)
    {
        var fields = new Hashtable { ["cipher"] = "aes-128-gcm", ["password"] = "test-only" };
        if (input != "omitted") fields["plugin"] = input == "null" ? null : input;
        ShadowsocksNode node = ParseShadowsocks(CreateNode("ss", fields));
        Assert.That(node.Plugin.Value, Is.Null);
        Assert.That(node.Plugin.Field, Is.EqualTo(input == "omitted" ? null : "plugin"));
        Assert.That(SingboxNodeEmitter.Emit(node).PluginOpts, Is.Null);
    }

    [Test]
    public void ShadowsocksEmitterRejectsUnsafeOrUninterpretedIr()
    {
        ShadowsocksNode node = ParseShadowsocks(CreateNode("ss", new Hashtable
        {
            ["cipher"] = "aes-128-gcm",
            ["password"] = "test-only",
            ["plugin"] = "obfs",
            ["plugin-opts"] = new Hashtable { ["mode"] = "http" }
        }));
        var plugin = node.Plugin.Value!;
        ShadowsocksNode[] invalidNodes =
        [
            node with { Port = 0 },
            node with { Password = "" },
            node with { Cipher = node.Cipher with { Value = "" } },
            node with { UdpOverTcpVersion = node.UdpOverTcpVersion with { Value = (ShadowsocksUotVersion)0 } },
            node with { Plugin = node.Plugin with { Value = plugin with { Host = plugin.Host with { Value = " " } } } },
            node with { Plugin = node.Plugin with { Value = plugin with { Host = plugin.Host with { Value = "x\ny=1" } } } },
            node with { Plugin = node.Plugin with { Value = plugin with { Mode = plugin.Mode with { Value = (ShadowsocksObfsMode)999 } } } }
        ];
        foreach (ShadowsocksNode invalid in invalidNodes)
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(invalid));
    }

    [Test]
    public void ShadowsocksPreservesSourceDiagnosticOrder()
    {
        var options = new Hashtable { ["mode"] = "bad", ["host"] = "bad\nhost" };
        var fields = new Hashtable
        {
            ["port"] = 0,
            ["cipher"] = "",
            ["password"] = "",
            ["udp_over_tcp"] = "bad",
            ["plugin"] = "obfs",
            ["plugin_opts"] = options
        };
        (Hashtable Target, string Field, object Value, string Error)[] fixes =
        [
            (fields, "port", 443, "端口格式无效: port"),
            (fields, "cipher", "aes-128-gcm", "缺失加密方式 (cipher 或 method)"),
            (fields, "password", "test-only", "缺失必填字段或为空: password"),
            (fields, "udp_over_tcp", true, "字段 'udp_over_tcp' 必须是 true 或 false"),
            (options, "host", "bing.com", "字段 'plugin-opts.host'"),
            (options, "mode", "http", "字段 'plugin-opts.mode'")
        ];
        foreach (var fix in fixes)
        {
            var result = new ShadowsocksConverter().Convert(CreateNode("ss", fields));
            Assert.That(result, Is.TypeOf<InvalidNode>());
            Assert.That(((InvalidNode)result).ErrorMessage,
                Does.StartWith("Shadowsocks 节点 'test-node' 解析失败 -> ").And.Contain(fix.Error));
            fix.Target[fix.Field] = fix.Value;
        }
        Assert.That(new ShadowsocksConverter().Convert(CreateNode("ss", fields)), Is.TypeOf<ConvertedNode>());
    }

    [TestCase("omitted", "443")]
    [TestCase("null", "443")]
    [TestCase(" ", "443")]
    [TestCase("443", "443")]
    [TestCase("443-443", "443:443")]
    [TestCase("443,8443", "443:443,8443:8443")]
    [TestCase("443,8443-8444", "443:443,8443:8444")]
    [TestCase("443-444,8443", "443:444,8443:8443")]
    [TestCase("1,65535,1", "1:1,65535:65535,1:1")]
    public void Hysteria2IrPreservesPortPrecedenceAndOutputShape(string input, string expected)
    {
        bool fallback = input is "omitted" or "null" or " ";
        var fields = new Hashtable { ["password"] = "test-only", ["port"] = fallback ? 443 : 0 };
        if (input != "omitted") fields["ports"] = input == "null" ? null : input;
        Hysteria2Node node = ParseHysteria2(CreateNode("hysteria2", fields));
        Hysteria2Outbound outbound = SingboxNodeEmitter.Emit(node);
        Assert.That(node.Ports.Field, Is.EqualTo(input == "omitted" ? "port" : "ports"));
        Assert.That(node.Ports.RuleId, Is.EqualTo(Hysteria2Semantics.Ports.Id));
        if (expected == "443")
        {
            Assert.That(node.Ports.Value[0].End, Is.Null);
            Assert.That(outbound.ServerPort, Is.EqualTo(443));
            Assert.That(outbound.ServerPorts, Is.Null);
        }
        else
        {
            Assert.That(outbound.ServerPort, Is.Null);
            Assert.That(outbound.ServerPorts, Is.EqualTo(expected.Split(',')));
            outbound.ServerPorts!.Clear();
            Assert.That(SingboxNodeEmitter.Emit(node).ServerPorts, Is.EqualTo(expected.Split(',')));
        }
    }

    [TestCase("omitted", null, null)]
    [TestCase("null", null, null)]
    [TestCase("", 30L, null)]
    [TestCase("20", 20L, null)]
    [TestCase("0", 30L, null)]
    [TestCase("30-15", 15L, 30L)]
    [TestCase("2-3", 5L, 5L)]
    public void Hysteria2IrKeepsFixedAndRangedHopOrigins(string input, long? minimum, long? maximum)
    {
        var fields = new Hashtable { ["password"] = "test-only" };
        if (input != "omitted") fields["hop_interval"] = input == "null" ? null : input;
        Hysteria2Node node = ParseHysteria2(CreateNode("hysteria2", fields));
        Assert.That(node.HopInterval.Field, Is.EqualTo(input == "omitted" ? null : "hop_interval"));
        Assert.That(node.HopInterval.RuleId, Is.EqualTo(Hysteria2Semantics.HopInterval.Id));
        Assert.That(node.HopInterval.Value?.MinimumSeconds, Is.EqualTo(minimum));
        Assert.That(node.HopInterval.Value?.MaximumSeconds, Is.EqualTo(maximum));
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
        {
            var tuned = (Hysteria2Outbound)OutboundTuningPolicy.Apply(
                SingboxNodeEmitter.Emit(node), BoxForgeProfile.Default.GetPlatform(platform));
            Assert.That(tuned.HopInterval, Is.EqualTo(minimum is null ? "30s" : minimum + "s"));
            Assert.That(tuned.HopIntervalMax, Is.EqualTo(minimum is null ? "60s" : maximum is null ? null : maximum + "s"));
        }
    }

    [TestCase("omitted", null)]
    [TestCase("null", null)]
    [TestCase("0", 0)]
    [TestCase("125000 Bps", 1)]
    public void Hysteria2IrKeepsBandwidthOriginAndTuningPriority(string input, int? expected)
    {
        var fields = new Hashtable { ["password"] = "test-only" };
        if (input != "omitted") fields["up"] = input == "null" ? null : input;
        Hysteria2Node node = ParseHysteria2(CreateNode("hysteria2", fields));
        Assert.That(node.UploadMegabitsPerSecond.Value, Is.EqualTo(expected));
        Assert.That(node.UploadMegabitsPerSecond.Field, Is.EqualTo(input == "omitted" ? null : "up"));
        Assert.That(node.UploadMegabitsPerSecond.RuleId, Is.EqualTo(Hysteria2Semantics.Upload.Id));
        Assert.That(node.DownloadMegabitsPerSecond.Field, Is.Null);
        Assert.That(node.BbrProfile.Value, Is.Null);
        var tuned = (Hysteria2Outbound)OutboundTuningPolicy.Apply(
            SingboxNodeEmitter.Emit(node), BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux));
        Assert.That(tuned.UpMbps, Is.EqualTo(expected));
        Assert.That(tuned.BbrProfile, Is.EqualTo(expected > 0 ? null : "standard"));
    }

    [TestCase(0)]
    [TestCase(600)]
    public void Hysteria2IrKeepsGeckoPacketSizeAliasesAndZeroDefault(int minimum)
    {
        Hysteria2Node node = ParseHysteria2(CreateNode("hysteria2", new Hashtable
        {
            ["password"] = "test-only",
            ["obfs"] = "gecko",
            ["obfs-password"] = " obfs-test-only ",
            ["obfs_min_packet_size"] = minimum,
            ["obfs_max_packet_size"] = 0
        }));
        var obfs = node.Obfuscation.Value!;
        Assert.That(node.Obfuscation.RuleId, Is.EqualTo(Hysteria2Semantics.Obfuscation.Id));
        Assert.That(obfs.Kind, Is.EqualTo(Hysteria2ObfuscationKind.Gecko));
        Assert.That(obfs.MinimumPacketSize.Value, Is.EqualTo(minimum == 0 ? null : (int?)minimum));
        Assert.That(obfs.MinimumPacketSize.Field, Is.EqualTo("obfs_min_packet_size"));
        Assert.That(obfs.MinimumPacketSize.RuleId, Is.EqualTo(Hysteria2Semantics.MinimumPacketSize.Id));
        Assert.That(obfs.MaximumPacketSize.Value, Is.Null);
        Assert.That(obfs.MaximumPacketSize.Field, Is.EqualTo("obfs_max_packet_size"));
        Assert.That(obfs.MaximumPacketSize.RuleId, Is.EqualTo(Hysteria2Semantics.MaximumPacketSize.Id));
        Assert.That(SingboxNodeEmitter.Emit(node).Obfs!.Password, Is.EqualTo(" obfs-test-only "));
    }

    [Test]
    public void Hysteria2EmitterRejectsContradictoryIr()
    {
        Hysteria2Node node = ParseHysteria2(CreateNode("hysteria2", new Hashtable
        {
            ["password"] = "test-only",
            ["obfs"] = "gecko",
            ["obfs-password"] = "test-only"
        }));
        Hysteria2Node[] invalidNodes =
        [
            node with { Tls = null! },
            node with { TlsEnabled = node.TlsEnabled with { Value = false } },
            node with { TlsEnabled = node.TlsEnabled with { Field = "tls", Value = null } },
            node with { Tls = node.Tls with { ClientFingerprint = node.Tls.ClientFingerprint with { Value = "chrome" } } },
            node with { Ports = node.Ports with { Value = [] } },
            node with { Ports = node.Ports with { Value = [new(0, null)] } },
            node with { Ports = node.Ports with { Value = [new(444, 443)] } },
            node with { UploadMegabitsPerSecond = node.UploadMegabitsPerSecond with { Value = -1 } },
            node with { HopInterval = node.HopInterval with { Value = new(4, null) } },
            node with { HopInterval = node.HopInterval with { Value = new(20, 10) } },
            node with { HopInterval = node.HopInterval with { Value = new(9223372037L, null) } },
            node with { BbrProfile = node.BbrProfile with { Value = (Hysteria2BbrProfile)999 } },
            node with { Obfuscation = node.Obfuscation with { Value = node.Obfuscation.Value! with { Password = "" } } },
            node with
            {
                Obfuscation = node.Obfuscation with
                {
                    Value = node.Obfuscation.Value! with
                    {
                        MinimumPacketSize = node.Obfuscation.Value!.MinimumPacketSize with { Value = 1201 }
                    }
                }
            },
            node with
            {
                Obfuscation = node.Obfuscation with
                {
                    Value = node.Obfuscation.Value! with
                    {
                        Kind = Hysteria2ObfuscationKind.Salamander,
                        MinimumPacketSize = node.Obfuscation.Value!.MinimumPacketSize with { Field = "obfs-min-packet-size" }
                    }
                }
            }
        ];
        foreach (Hysteria2Node invalid in invalidNodes)
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(invalid));
    }

    [Test]
    public void Hysteria2PreservesSourceDiagnosticOrder()
    {
        var fields = new Hashtable
        {
            ["password"] = "",
            ["ports"] = "0",
            ["obfs"] = "gecko",
            ["obfs-password"] = "",
            ["obfs_min_packet_size"] = -1,
            ["obfs_max_packet_size"] = 2049,
            ["hop_interval"] = "bad",
            ["bbr_profile"] = "bad",
            ["up"] = "bad",
            ["down"] = "bad",
            ["skip-cert-verify"] = "bad"
        };
        (string Field, object Value, string Error)[] fixes =
        [
            ("ports", "4000-4001", "ports 包含无效端口"),
            ("obfs_min_packet_size", 0, "字段 'obfs-min-packet-size'"),
            ("obfs_max_packet_size", 1200, "字段 'obfs-max-packet-size'"),
            ("obfs-password", "test-only", "字段 'obfs-password'"),
            ("hop_interval", "20", "字段 'hop-interval'"),
            ("bbr_profile", "aggressive", "字段 'bbr-profile'"),
            ("up", 100, "字段 'up'"),
            ("down", 80, "字段 'down'"),
            ("password", "test-only", "缺失必填字段或为空: password"),
            ("skip-cert-verify", false, "字段 'skip-cert-verify'")
        ];
        foreach (var fix in fixes)
        {
            var result = new Hysteria2Converter().Convert(CreateNode("hysteria2", fields));
            Assert.That(result, Is.TypeOf<InvalidNode>());
            Assert.That(((InvalidNode)result).ErrorMessage,
                Does.StartWith("Hysteria2 节点 'test-node' 解析失败 -> ").And.Contain(fix.Error));
            fields[fix.Field] = fix.Value;
        }
        Assert.That(new Hysteria2Converter().Convert(CreateNode("hysteria2", fields)), Is.TypeOf<ConvertedNode>());
    }

    [Test]
    public void EmittingHysteria2ForMultiplePlatformsDoesNotMutatePortsOrTls()
    {
        Hysteria2Node node = ParseHysteria2(CreateNode("hysteria2", new Hashtable
        {
            ["password"] = " test-only ",
            ["ports"] = "443-444,8443",
            ["hop_interval"] = "20",
            ["bbr_profile"] = "aggressive",
            ["alpn"] = new[] { "h3" }
        }));
        Assert.That(node.BbrProfile.Field, Is.EqualTo("bbr_profile"));
        Assert.That(node.BbrProfile.RuleId, Is.EqualTo(Hysteria2Semantics.BbrProfile.Id));
        Assert.That(node.TlsEnabled.RuleId, Is.EqualTo(Hysteria2Semantics.Tls.Id));
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
        {
            var outbound = (Hysteria2Outbound)OutboundTuningPolicy.Apply(
                SingboxNodeEmitter.Emit(node), BoxForgeProfile.Default.GetPlatform(platform));
            Assert.That(outbound.ServerPorts, Is.EqualTo(new[] { "443:444", "8443:8443" }));
            Assert.That(outbound.HopInterval, Is.EqualTo("20s"));
            Assert.That(outbound.HopIntervalMax, Is.Null);
            Assert.That(outbound.BbrProfile, Is.EqualTo("aggressive"));
            Assert.That(outbound.Password, Is.EqualTo(" test-only "));
            Assert.That(outbound.Tls!.Alpn, Is.EqualTo(new[] { "h3" }));
            outbound.ServerPorts!.Clear();
            outbound.Tls.Alpn!.Clear();
        }
        Assert.That(node.Ports.Value, Has.Count.EqualTo(2));
        Assert.That(node.Tls.ApplicationProtocols.Value, Is.EqualTo(new[] { "h3" }));
    }

    [TestCase("idle-session-timeout", "30", "30s")]
    [TestCase("idle_session_timeout", "45s", "45s")]
    [TestCase("idle-timeout", ".5s", ".5s")]
    [TestCase("idle_timeout", "1μs", "1μs")]
    [TestCase("idle-session-check-interval", "30", "30s")]
    [TestCase("idle_session_check_interval", "250ms", "250ms")]
    public void AnyTlsIrRetainsDurationMeaningAndActualAlias(string field, string value, string expected)
    {
        AnyTlsNode node = ParseAnyTls(CreateNode("anytls", new Hashtable
        {
            ["password"] = "test-only",
            [field] = value
        }));
        bool isInterval = field.Contains("check", StringComparison.Ordinal);
        SourceValue<string?> duration = isInterval ? node.IdleCheckInterval : node.IdleTimeout;
        Assert.That(duration.Value, Is.EqualTo(expected));
        Assert.That(duration.Field, Is.EqualTo(field));
        Assert.That(duration.RuleId, Is.EqualTo(isInterval ? AnyTlsSemantics.IdleCheckInterval.Id : AnyTlsSemantics.IdleTimeout.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnyTlsIrKeepsOmissionSeparateFromExplicitNull(bool explicitNull)
    {
        var fields = new Hashtable { ["password"] = "test-only" };
        if (explicitNull)
        {
            fields["idle_timeout"] = null;
            fields["idle_session_check_interval"] = null;
            fields["min_idle_session"] = null;
            fields["client_metadata"] = null;
        }
        AnyTlsNode node = ParseAnyTls(CreateNode("anytls", fields));
        Assert.Multiple(() =>
        {
            Assert.That(node.IdleTimeout.Value, Is.Null);
            Assert.That(node.IdleTimeout.Field, Is.EqualTo(explicitNull ? "idle_timeout" : null));
            Assert.That(node.IdleCheckInterval.Value, Is.Null);
            Assert.That(node.IdleCheckInterval.Field, Is.EqualTo(explicitNull ? "idle_session_check_interval" : null));
            Assert.That(node.MinimumIdleSessions.Value, Is.Null);
            Assert.That(node.MinimumIdleSessions.Field, Is.EqualTo(explicitNull ? "min_idle_session" : null));
            Assert.That(node.ClientMetadata.Value, Is.Null);
            Assert.That(node.ClientMetadata.Field, Is.EqualTo(explicitNull ? "client_metadata" : null));
        });
        string json = SerializeOutbound(SingboxNodeEmitter.Emit(node));
        Assert.That(json, Does.Not.Contain("idle_session").And.Not.Contain("client_metadata"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnyTlsIrKeepsProtocolTlsDefaultDistinctFromExplicitTls(bool explicitTls)
    {
        var fields = new Hashtable { ["password"] = "test-only" };
        if (explicitTls) fields["tls"] = true;
        AnyTlsNode node = ParseAnyTls(CreateNode("anytls", fields));
        Assert.That(node.TlsEnabled.Field, Is.EqualTo(explicitTls ? "tls" : null));
        Assert.That(node.TlsEnabled.Value, Is.EqualTo(explicitTls ? true : (bool?)null));
        Assert.That(node.TlsEnabled.RuleId, Is.EqualTo(AnyTlsSemantics.Tls.Id));
        Assert.That(SingboxNodeEmitter.Emit(node).Tls!.Enabled, Is.True);
    }

    [TestCase(65479, true)]
    [TestCase(65480, false)]
    public void AnyTlsMetadataKeepsItsUtf8FrameBoundary(int bytes, bool accepted)
    {
        string metadata = new string('中', 21826) + new string('x', bytes - 65478);
        NodeConversionResult result = new AnyTlsConverter().Convert(CreateNode("anytls", new Hashtable
        {
            ["password"] = "test-only",
            ["client_metadata"] = metadata
        }));
        Assert.That(result is ConvertedNode, Is.EqualTo(accepted));
        if (result is ConvertedNode converted)
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(((AnyTlsOutbound)converted.Outbound).ClientMetadata!), Is.EqualTo(bytes));
    }

    [Test]
    public void AnyTlsEmitterRejectsInconsistentOrUnsafeIr()
    {
        AnyTlsNode node = ParseAnyTls(CreateNode("anytls", new Hashtable { ["password"] = "test-only" }));
        AnyTlsNode[] invalidNodes =
        [
            node with { Tls = null! },
            node with { TlsEnabled = node.TlsEnabled with { Value = false } },
            node with { TlsEnabled = node.TlsEnabled with { Field = "tls", Value = null } },
            node with { MinimumIdleSessions = node.MinimumIdleSessions with { Value = -1 } },
            node with { IdleTimeout = node.IdleTimeout with { Value = "0s" } },
            node with { IdleCheckInterval = node.IdleCheckInterval with { Value = "2562048h" } },
            node with { ClientMetadata = node.ClientMetadata with { Value = "x\nv=1" } },
            node with { ClientMetadata = node.ClientMetadata with { Value = new string('x', 65480) } },
            node with
            {
                Tls = node.Tls with
                {
                    Reality = new RealitySettings(
                        new(RealityPublicKey, "reality-opts.public-key", TlsSemantics.RealityPublicKey.Id),
                        new("00", "reality-opts.short-id", TlsSemantics.RealityShortId.Id))
                }
            }
        ];
        foreach (AnyTlsNode invalid in invalidNodes)
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(invalid));
    }

    [Test]
    public void AnyTlsPreservesDiagnosticOrderAcrossSessionMetadataAndTlsFailures()
    {
        var fields = new Hashtable
        {
            ["port"] = 0,
            ["password"] = "",
            ["idle_timeout"] = "0s",
            ["idle_session_check_interval"] = "0s",
            ["min_idle_session"] = -1,
            ["client_metadata"] = "x\nv=1",
            ["client-fingerprint"] = "random"
        };
        (string Field, object Value, string Error)[] fixes =
        [
            ("port", 443, "端口格式无效: port"),
            ("password", "test-only", "缺失必填字段或为空: password"),
            ("idle_timeout", "30", "字段 'idle_timeout'"),
            ("idle_session_check_interval", "30", "字段 'idle_session_check_interval'"),
            ("min_idle_session", 0, "字段 'min-idle-session'"),
            ("client_metadata", "", "字段 'client-metadata'"),
            ("client-fingerprint", "none", "字段 'client-fingerprint'")
        ];
        foreach (var fix in fixes)
        {
            var result = new AnyTlsConverter().Convert(CreateNode("anytls", fields));
            Assert.That(result, Is.TypeOf<InvalidNode>());
            Assert.That(((InvalidNode)result).ErrorMessage,
                Does.StartWith("AnyTLS 节点 'test-node' 解析失败 -> ").And.Contain(fix.Error));
            fields[fix.Field] = fix.Value;
        }
        Assert.That(new AnyTlsConverter().Convert(CreateNode("anytls", fields)), Is.TypeOf<ConvertedNode>());
    }

    [Test]
    public void EmittingAnyTlsForMultiplePlatformsPreservesExplicitZeroAndEmptyMetadata()
    {
        AnyTlsNode node = ParseAnyTls(CreateNode("anytls", new Hashtable
        {
            ["password"] = " test-only ",
            ["idle_timeout"] = "15",
            ["idle_session_check_interval"] = ".5s",
            ["min_idle_session"] = 0,
            ["client_metadata"] = "",
            ["alpn"] = new[] { "h2" }
        }));
        Assert.That(node.MinimumIdleSessions.Field, Is.EqualTo("min_idle_session"));
        Assert.That(node.MinimumIdleSessions.RuleId, Is.EqualTo(AnyTlsSemantics.MinimumIdleSessions.Id));
        Assert.That(node.ClientMetadata.Field, Is.EqualTo("client_metadata"));
        Assert.That(node.ClientMetadata.RuleId, Is.EqualTo(AnyTlsSemantics.ClientMetadata.Id));
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
        {
            var outbound = (AnyTlsOutbound)OutboundTuningPolicy.Apply(
                SingboxNodeEmitter.Emit(node), BoxForgeProfile.Default.GetPlatform(platform));
            Assert.That(outbound.Password, Is.EqualTo(" test-only "));
            Assert.That(outbound.IdleSessionTimeout, Is.EqualTo("15s"));
            Assert.That(outbound.IdleSessionCheckInterval, Is.EqualTo(".5s"));
            Assert.That(outbound.MinIdleSession, Is.Zero);
            Assert.That(outbound.ClientMetadata, Is.Empty);
            Assert.That(outbound.Tls!.Alpn, Is.EqualTo(new[] { "h2" }));
            Assert.That(SerializeOutbound(outbound), Does.Contain("\"client_metadata\": \"\""));
            outbound.Tls.Alpn!.Clear();
        }
        Assert.That(node.Tls.ApplicationProtocols.Value, Is.EqualTo(new[] { "h2" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TrojanIrDistinguishesProtocolRequiredTlsFromExplicitTls(bool explicitTls)
    {
        var fields = new Hashtable { ["password"] = " test-only " };
        if (explicitTls) fields["tls"] = true;
        TrojanNode node = ParseTrojan(CreateNode("trojan", fields));
        TrojanOutbound outbound = SingboxNodeEmitter.Emit(node);

        Assert.Multiple(() =>
        {
            Assert.That(node.TlsEnabled.Value, Is.EqualTo(explicitTls ? true : (bool?)null));
            Assert.That(node.TlsEnabled.Field, Is.EqualTo(explicitTls ? "tls" : null));
            Assert.That(node.TlsEnabled.RuleId, Is.EqualTo(TlsSemantics.TrojanActivation.Id));
            Assert.That(outbound.Tls!.Enabled, Is.True);
            Assert.That(outbound.Password, Is.EqualTo(" test-only "));
        });
    }

    [TestCase(false)]
    [TestCase(null)]
    public void TrojanStillRejectsExplicitDisabledOrNullTls(bool? value)
    {
        NodeConversionResult result = new TrojanConverter().Convert(CreateNode("trojan", new Hashtable
        {
            ["password"] = "test-only",
            ["tls"] = value
        }));
        Assert.That(result, Is.TypeOf<InvalidNode>());
        Assert.That(((InvalidNode)result).ErrorMessage, Is.EqualTo(
            "Trojan 节点 'test-node' 解析失败 -> 字段 'tls' 为 false 与此协议要求的 TLS 连接冲突"));
    }

    [Test]
    public void TrojanEmitterRejectsMissingTlsAndContradictoryActivation()
    {
        TrojanNode node = ParseTrojan(CreateNode("trojan", new Hashtable { ["password"] = "test-only" }));
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(node with { Tls = null! }));
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(node with
            {
                TlsEnabled = node.TlsEnabled with { Value = false }
            }));
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(node with
            {
                TlsEnabled = node.TlsEnabled with { Field = "tls", Value = null }
            }));
        });
    }

    [TestCase("vless", false)]
    [TestCase("trojan", false)]
    [TestCase("vless", true)]
    [TestCase("trojan", true)]
    public void TlsIrRetainsAliasesAndNestedRealityPaths(string protocol, bool aliases)
    {
        string nameField = aliases ? "servername" : "sni";
        string fingerprintField = aliases ? "client_fingerprint" : "client-fingerprint";
        string realityField = aliases ? "reality_opts" : "reality-opts";
        string keyField = aliases ? "public_key" : "public-key";
        string shortIdField = aliases ? "short_id" : "short-id";
        TlsSettings tls = ParseTls(protocol, new Hashtable
        {
            [nameField] = " tls.example.com ",
            [fingerprintField] = "chrome",
            [realityField] = new Hashtable { [keyField] = RealityPublicKey, [shortIdField] = " Aa00 " }
        });
        OutboundTls emitted = SingboxNodeEmitter.EmitTls(tls)!;
        Assert.Multiple(() =>
        {
            Assert.That(tls.ServerName.Field, Is.EqualTo(nameField));
            Assert.That(tls.ServerName.RuleId, Is.EqualTo(TlsSemantics.ServerName.Id));
            Assert.That(emitted.ServerName, Is.EqualTo("tls.example.com"));
            Assert.That(tls.ClientFingerprint.Field, Is.EqualTo(fingerprintField));
            Assert.That(tls.ClientFingerprint.RuleId, Is.EqualTo(TlsSemantics.ClientFingerprint.Id));
            Assert.That(emitted.Utls!.Fingerprint, Is.EqualTo("chrome"));
            Assert.That(tls.Reality!.PublicKey.Field, Is.EqualTo($"{realityField}.{keyField}"));
            Assert.That(tls.Reality.PublicKey.RuleId, Is.EqualTo(TlsSemantics.RealityPublicKey.Id));
            Assert.That(emitted.Reality!.PublicKey, Is.EqualTo(RealityPublicKey));
            Assert.That(tls.Reality.ShortId.Field, Is.EqualTo($"{realityField}.{shortIdField}"));
            Assert.That(tls.Reality.ShortId.RuleId, Is.EqualTo(TlsSemantics.RealityShortId.Id));
            Assert.That(emitted.Reality.ShortId, Is.EqualTo("Aa00"));
        });
    }

    [TestCase("vless", "omitted")]
    [TestCase("trojan", "omitted")]
    [TestCase("vless", "null")]
    [TestCase("trojan", "null")]
    [TestCase("vless", "empty")]
    [TestCase("trojan", "empty")]
    public void TlsIrKeepsNameFallbackAndItsOrigin(string protocol, string input)
    {
        var fields = new Hashtable();
        if (input != "omitted") fields["servername"] = input == "null" ? null : " ";
        TlsSettings tls = ParseTls(protocol, fields);
        Assert.That(tls.ServerName.Value, Is.EqualTo("node.example.com"));
        Assert.That(tls.ServerName.Field, Is.EqualTo(input == "omitted" ? null : "servername"));
        Assert.That(SingboxNodeEmitter.EmitTls(tls)!.ServerName, Is.EqualTo("node.example.com"));
    }

    [TestCase("vless", "omitted", null)]
    [TestCase("trojan", "omitted", null)]
    [TestCase("vless", "null", null)]
    [TestCase("trojan", "null", null)]
    [TestCase("vless", "false", false)]
    [TestCase("trojan", "false", false)]
    [TestCase("vless", "true", true)]
    [TestCase("trojan", "true", true)]
    public void TlsIrKeepsCertificateValidationPresence(string protocol, string input, bool? expected)
    {
        var fields = new Hashtable();
        if (input != "omitted") fields["skip-cert-verify"] = expected;
        TlsSettings tls = ParseTls(protocol, fields);
        Assert.That(tls.AllowInvalidCertificate.Value, Is.EqualTo(expected));
        Assert.That(tls.AllowInvalidCertificate.Field, Is.EqualTo(input == "omitted" ? null : "skip-cert-verify"));
        Assert.That(tls.AllowInvalidCertificate.RuleId, Is.EqualTo(TlsSemantics.CertificateValidation.Id));
        Assert.That(SingboxNodeEmitter.EmitTls(tls)!.Insecure, Is.EqualTo(expected));
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    public void TlsIrKeepsAlpnOmissionSeparateFromAnExplicitEmptyList(string protocol)
    {
        TlsSettings absent = ParseTls(protocol, new Hashtable());
        TlsSettings empty = ParseTls(protocol, new Hashtable { ["alpn"] = Array.Empty<string>() });
        Assert.Multiple(() =>
        {
            Assert.That(absent.ApplicationProtocols.Value, Is.Null);
            Assert.That(absent.ApplicationProtocols.Field, Is.Null);
            Assert.That(empty.ApplicationProtocols.Value, Is.Empty);
            Assert.That(empty.ApplicationProtocols.Field, Is.EqualTo("alpn"));
            Assert.That(empty.ApplicationProtocols.RuleId, Is.EqualTo(TlsSemantics.ApplicationProtocols.Id));
        });
    }

    [TestCase("vless", false, null)]
    [TestCase("trojan", false, null)]
    [TestCase("vless", true, null)]
    [TestCase("trojan", true, null)]
    [TestCase("vless", true, "")]
    [TestCase("trojan", true, "")]
    [TestCase("vless", true, "none")]
    [TestCase("trojan", true, "none")]
    public void TlsIrKeepsNativeFingerprintOrigin(string protocol, bool supplied, string? value)
    {
        var fields = new Hashtable();
        if (supplied) fields["client_fingerprint"] = value;
        TlsSettings tls = ParseTls(protocol, fields);
        Assert.That(tls.ClientFingerprint.Value, Is.Null);
        Assert.That(tls.ClientFingerprint.Field, Is.EqualTo(supplied ? "client_fingerprint" : null));
        Assert.That(SingboxNodeEmitter.EmitTls(tls)!.Utls, Is.Null);
    }

    [Test]
    public void EmittingTrojanForMultiplePlatformsDoesNotMutateIr()
    {
        TrojanNode node = ParseTrojan(CreateNode("trojan", new Hashtable
        {
            ["password"] = "test-only",
            ["alpn"] = new[] { "h2", "http/1.1", "h2" }
        }));
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
        {
            var outbound = (TrojanOutbound)OutboundTuningPolicy.Apply(
                SingboxNodeEmitter.Emit(node), BoxForgeProfile.Default.GetPlatform(platform));
            Assert.That(outbound.Tls!.Alpn, Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
            outbound.Tls.Alpn!.Clear();
        }
        Assert.That(node.Tls.ApplicationProtocols.Value, Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
    }

    [TestCase("omitted", null)]
    [TestCase("null", null)]
    [TestCase("false", false)]
    [TestCase("true", true)]
    public void VlessIrDistinguishesTlsOmissionFromExplicitValues(string input, bool? expected)
    {
        var fields = new Hashtable { ["uuid"] = "test-only" };
        if (input != "omitted") fields["tls"] = expected;
        VlessNode node = ParseVless(CreateNode("vless", fields));

        Assert.Multiple(() =>
        {
            Assert.That(node.TlsEnabled.Value, Is.EqualTo(expected));
            Assert.That(node.TlsEnabled.Field, Is.EqualTo(input == "omitted" ? null : "tls"));
            Assert.That(node.TlsEnabled.RuleId, Is.EqualTo(VlessSemantics.Tls.Id));
            Assert.That(node.Tls is not null, Is.EqualTo(expected == true));
        });
        node.Validate();
    }

    [TestCase(null, null, "Xudp")]
    [TestCase("packet-encoding", null, "Xudp")]
    [TestCase("packet_encoding", "packet", "PacketAddress")]
    [TestCase("packet-encoding", "PACKET", "Xudp")]
    public void VlessIrRetainsDatagramOriginAndEffectiveMeaning(
        string? field, string? value, string expected)
    {
        var fields = new Hashtable { ["uuid"] = "test-only" };
        if (field is not null) fields[field] = value;
        VlessNode node = ParseVless(CreateNode("vless", fields));

        Assert.Multiple(() =>
        {
            Assert.That(node.DatagramEncoding.Value.ToString(), Is.EqualTo(expected));
            Assert.That(node.DatagramEncoding.Field, Is.EqualTo(field));
            Assert.That(node.DatagramEncoding.RuleId, Is.EqualTo(VlessSemantics.DatagramEncoding.Id));
            Assert.That(node.Flow.Value, Is.EqualTo(VlessFlow.None));
            Assert.That(node.Flow.Field, Is.Null);
        });
    }

    [Test]
    public void VlessIrKeepsExplicitFlowOriginAfterNormalization()
    {
        VlessNode node = ParseVless(CreateNode("vless", new Hashtable
        {
            ["uuid"] = "test-only",
            ["flow"] = "xtls-rprx-vision-udp443"
        }));

        Assert.That(node.Flow.Value, Is.EqualTo(VlessFlow.Vision));
        Assert.That(node.Flow.Field, Is.EqualTo("flow"));
        Assert.That(node.Flow.RuleId, Is.EqualTo(VlessSemantics.Flow.Id));
    }

    [Test]
    public void VlessIrRejectsTlsContradictionsBeforeEmission()
    {
        VlessNode node = ParseVless(CreateNode("vless", new Hashtable
        {
            ["uuid"] = "test-only",
            ["tls"] = true
        }));
        VlessNode missingTls = node with { Tls = null };
        VlessNode disabledTls = node with { TlsEnabled = node.TlsEnabled with { Value = false } };
        VlessNode invalidReality = node with
        {
            Tls = node.Tls! with
            {
                Reality = new RealitySettings(
                    new(RealityPublicKey, "reality-opts.public-key", TlsSemantics.RealityPublicKey.Id),
                    new("00", "reality-opts.short-id", TlsSemantics.RealityShortId.Id))
            }
        };

        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(missingTls));
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(disabledTls));
            Assert.Throws<InvalidOperationException>(() => SingboxNodeEmitter.Emit(invalidReality));
        });
    }

    [Test]
    public void EmittingVlessForMultiplePlatformsDoesNotMutateIr()
    {
        VlessNode node = ParseVless(CreateNode("vless", new Hashtable
        {
            ["uuid"] = "test-only",
            ["tls"] = true,
            ["skip-cert-verify"] = false,
            ["alpn"] = new[] { "h2", "http/1.1", "h2" }
        }));
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
        {
            var outbound = (VlessOutbound)OutboundTuningPolicy.Apply(
                SingboxNodeEmitter.Emit(node), BoxForgeProfile.Default.GetPlatform(platform));
            Assert.That(outbound.Tls!.Insecure, Is.False);
            Assert.That(outbound.Tls.Alpn, Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
            outbound.Tls.Alpn!.Clear();
        }
        Assert.That(node.Tls!.ApplicationProtocols.Value, Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
    }

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
            ProxyOutbound tuned = OutboundTuningPolicy.Apply(source, BoxForgeProfile.Default.GetPlatform(platform));
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

    private static ShadowsocksNode ParseShadowsocks(ClashProxyNode source)
    {
        string name = source.GetRequiredString("name");
        ShadowsocksFrontend.Schema.Validate(source);
        return ShadowsocksFrontend.ParseValidated(source, name);
    }

    private static Hysteria2Node ParseHysteria2(ClashProxyNode source)
    {
        string name = source.GetRequiredString("name");
        Hysteria2Frontend.Schema.Validate(source);
        return Hysteria2Frontend.ParseValidated(source, name);
    }

    private static AnyTlsNode ParseAnyTls(ClashProxyNode source)
    {
        string name = source.GetRequiredString("name");
        AnyTlsFrontend.Schema.Validate(source);
        return AnyTlsFrontend.ParseValidated(source, name);
    }

    private static VlessNode ParseVless(ClashProxyNode source)
    {
        string name = source.GetRequiredString("name");
        VlessFrontend.Schema.Validate(source);
        return VlessFrontend.ParseValidated(source, name);
    }

    private static TrojanNode ParseTrojan(ClashProxyNode source)
    {
        string name = source.GetRequiredString("name");
        TrojanFrontend.Schema.Validate(source);
        return TrojanFrontend.ParseValidated(source, name);
    }

    private static TlsSettings ParseTls(string protocol, Hashtable extra)
    {
        var fields = new Hashtable(extra)
        {
            [protocol == "vless" ? "uuid" : "password"] = "test-only",
            ["tls"] = true
        };
        ClashProxyNode source = CreateNode(protocol, fields);
        return protocol == "vless" ? ParseVless(source).Tls! : ParseTrojan(source).Tls;
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
