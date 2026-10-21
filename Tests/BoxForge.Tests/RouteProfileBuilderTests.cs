using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Models.Intermediate;
using System.Text.Json;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Tests;

[TestFixture]
public sealed class RouteProfileBuilderTests
{
    private const string DustinWinRuleSetUrlTemplate =
        "https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs";
    private const string MetaCubeXRuleSetUrlBase =
        "https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/sing/geo/geosite/";
    private static readonly string[] DustinWinRuleSetTags =
    [
        RuleSetTags.Ai,
        RuleSetTags.Spotify,
        RuleSetTags.Games,
        RuleSetTags.Cn,
        RuleSetTags.CnIp
    ];

    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.Ipv4Only)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.Ipv4Only)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.Ipv4Only)]
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.DualStack)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.DualStack)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.DualStack)]
    public void RoutePlanSnapshotsPolicyAndEmitsIndependentConfigurations(TargetPlatform platform, AddressFamilyPolicy addressFamily)
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            platforms = new Dictionary<string, object>
            {
                [platform.ToString()] = new { dns = new { address_family = addressFamily.ToString() } }
            }
        })!.AsObject());
        var settings = profile.GetPlatform(platform);
        var builder = new RouteProfileBuilder(profile);
        var plan = builder.Plan(platform);
        string expected = JsonSerializer.Serialize(SingboxRouteEmitter.Emit(builder.Plan(platform)));
        var first = SingboxRouteEmitter.Emit(plan);
        Assert.That(JsonSerializer.Serialize(first), Is.EqualTo(expected));
        foreach (SingboxRuleSet source in first.RuleSet)
            source.Tag!.Clear();
        first.RuleSet.Clear();
        foreach (RouteRule rule in first.Rules)
            ClearMatchers(rule);
        first.Rules.Clear();
        var changedProfile = profile.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = false } }
        })!.AsObject());
        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Serialize(SingboxRouteEmitter.Emit(plan)), Is.EqualTo(expected));
            Assert.That(plan.AddressFamily, Is.EqualTo(addressFamily));
            Assert.That(plan.UseDirectForwarding, Is.EqualTo(settings.UseDirectForwarding));
            Assert.That(plan.TailscaleEnabled, Is.True);
            Assert.That(builder.Plan(platform).TailscaleEnabled, Is.True);
            Assert.That(builder.Plan(platform, changedProfile).TailscaleEnabled, Is.False);
            Assert.That(JsonSerializer.Serialize(plan.PriorityServices), Is.EqualTo(JsonSerializer.Serialize(profile.Services.Where(service => service.PrecedesDomesticRoutes))));
            Assert.That(JsonSerializer.Serialize(plan.OtherServices), Is.EqualTo(JsonSerializer.Serialize(profile.Services.Where(service => !service.PrecedesDomesticRoutes))));
            Assert.That(plan.RuleSets.SelectMany(source => source.Tags), Is.EqualTo(profile.RuleSets.SelectMany(source => source.Tags)));
        });

        static void ClearMatchers(RouteRule rule)
        {
            if (rule.Rules is { } children)
            {
                foreach (RouteRule child in children)
                    ClearMatchers(child);
                children.Clear();
            }
            rule.Inbound?.Clear();
            rule.Protocol?.Clear();
            rule.Port?.Clear();
            rule.Network?.Clear();
            rule.RuleSet?.Clear();
            rule.IpCidr?.Clear();
            rule.PreferredBy?.Clear();
            rule.Sniffer?.Clear();
        }
    }

    [TestCase("service_reference")]
    [TestCase("service_priority")]
    [TestCase("duplicate_service")]
    [TestCase("duplicate_rule_set")]
    [TestCase("missing_base_rule_set")]
    public void InvalidRouteReferencesAndClassificationFailBeforeEmission(string fault)
    {
        var plan = CreateBuilder().Plan(TargetPlatform.Linux);
        plan = fault switch
        {
            "service_reference" => plan with { PriorityServices = [plan.PriorityServices[0] with { RuleSets = ["missing"] }, .. plan.PriorityServices.Skip(1)] },
            "service_priority" => plan with { PriorityServices = [plan.PriorityServices[0] with { PrecedesDomesticRoutes = false }, .. plan.PriorityServices.Skip(1)] },
            "duplicate_service" => plan with { OtherServices = [.. plan.OtherServices, plan.PriorityServices[0]] },
            "duplicate_rule_set" => plan with { RuleSets = [.. plan.RuleSets, plan.RuleSets[0]] },
            _ => plan with
            {
                RuleSets = plan.RuleSets.Select(source => source with { Tags = [.. source.Tags.Where(tag => tag != RuleSetTags.Ads)] })
                .Where(source => !source.Tags.IsEmpty).ToArray()
            }
        };
        Assert.Throws<InvalidOperationException>(() => SingboxRouteEmitter.Emit(plan));
    }

    [Test]
    public void DirectForwardingLayersRespectExplicitPreAndPostSniffPhases()
    {
        RouteConfig android = SingboxRouteEmitter.Emit(CreateBuilder().Plan(TargetPlatform.Android));
        RouteConfig windows = SingboxRouteEmitter.Emit(CreateBuilder().Plan(TargetPlatform.Windows));
        RouteConfig linux = SingboxRouteEmitter.Emit(CreateBuilder().Plan(TargetPlatform.Linux));

        List<RouteRule> androidDirect = DirectRules(android);
        List<RouteRule> preSniffDirect = PreSniffDirectRules(android);
        List<RouteRule> windowsBridge = BridgeRules(windows);
        List<RouteRule> linuxBridge = BridgeRules(linux);
        List<RouteRule> linuxBypass = linux.Rules
            .Where(rule => rule.Action == RouteRuleAction.Bypass)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(androidDirect, Has.Count.EqualTo(6));
            Assert.That(preSniffDirect, Has.Count.EqualTo(2));
            Assert.That(
                preSniffDirect.Any(rule => rule.IpIsPrivate == true),
                Is.True);
            Assert.That(
                preSniffDirect.Any(rule =>
                    rule.IpCidr?.SequenceEqual(["223.5.5.5/32"]) == true),
                Is.True);
            Assert.That(DirectRules(windows), Has.Count.EqualTo(androidDirect.Count));
            Assert.That(DirectRules(linux), Has.Count.EqualTo(androidDirect.Count));
            Assert.That(windowsBridge, Is.Empty);
            Assert.That(linuxBridge, Has.Count.EqualTo(6));
            Assert.That(linuxBypass, Has.Count.EqualTo(6));
            Assert.That(
                windows.Rules.Any(rule => rule.Action == RouteRuleAction.Bypass),
                Is.False);
            Assert.That(
                android.Rules.Any(rule =>
                    rule.Action == RouteRuleAction.Bypass
                    || rule.Outbound == SingboxTags.BridgeOutbound),
                Is.False);
            Assert.That(windows.Rules.Any(ContainsBridgePreferenceGate), Is.False);
            Assert.That(windows.Rules.Select(MatcherKey),
                Is.EqualTo(android.Rules.Select(MatcherKey)));
            Assert.That(
                linuxBridge.All(ContainsBridgePreferenceGate),
                Is.True);
            Assert.That(
                linuxBridge.All(ContainsTunPreMatchGate),
                Is.True);
            Assert.That(
                linuxBypass.All(ContainsTunPreMatchGate),
                Is.True);
            Assert.That(linuxBypass.All(rule => rule.Outbound == null), Is.True);
            Assert.That(
                linuxBridge.Count(rule => linux.Rules.IndexOf(rule)
                    < FindFirstSniffIndex(linux)),
                Is.EqualTo(2));
            Assert.That(
                linuxBridge.Count(rule => linux.Rules.IndexOf(rule)
                    > FindSniffIndex(linux, "udp")),
                Is.EqualTo(4));
            Assert.That(
                linuxBypass.Count(rule => linux.Rules.IndexOf(rule)
                    > FindSniffIndex(linux, "udp")),
                Is.EqualTo(4));
            Assert.That(
                linuxBridge.Where(rule => linux.Rules.IndexOf(rule)
                    > FindSniffIndex(linux, "udp"))
                    .All(rule => ContainsNetwork(rule, "udp")),
                Is.True);
        });

        AssertDirectLayerOrder(linux);
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void TunKeepsEarlyTailscaleGateWithoutMixedResolutionStages(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(new RouteProfileBuilder(BoxForgeProfile.Default).Plan(platform));
        RouteRule gate = route.Rules.Single(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleEndpoint) == true);
        int privateDirectIndex = route.Rules.FindIndex(rule =>
            rule.IpIsPrivate == true && rule.Outbound == SingboxTags.DirectOutbound);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Inbound, Is.EqualTo(new[] { SingboxTags.TunInbound }));
            Assert.That(gate.Outbound, Is.EqualTo(SingboxTags.TailscaleEndpoint));
            Assert.That(route.Rules.IndexOf(gate), Is.LessThan(privateDirectIndex));
            Assert.That(route.Rules.Any(rule => rule.Action == RouteRuleAction.Resolve), Is.False);
            Assert.That(SingboxRouteEmitter.Emit(CreateBuilder().Plan(platform)).Rules.Any(rule =>
                rule.PreferredBy?.Contains(SingboxTags.TailscaleEndpoint) == true), Is.False);
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllIpv6IsRejectedBeforeSniffWithLateFallbackPreserved(
        TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreateBuilder().Plan(platform));
        List<RouteRule> ipv6Rejects = route.Rules.Where(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.IpVersion == 6).ToList();
        RouteRule earlyReject = ipv6Rejects[0];
        int earlyRejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject && rule.IpVersion == 6);
        RouteRule lateReject = ipv6Rejects[1];
        int lateRejectIndex = route.Rules.FindLastIndex(rule =>
            rule.Action == RouteRuleAction.Reject && rule.IpVersion == 6);

        Assert.Multiple(() =>
        {
            Assert.That(
                new[]
                {
                    earlyRejectIndex,
                    FindFirstSniffIndex(route),

                    lateRejectIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(ipv6Rejects, Has.Count.EqualTo(2));
            Assert.That(earlyReject.Type, Is.Null);
            Assert.That(earlyReject.NoDrop, Is.Null);
            Assert.That(lateReject.Type, Is.Null);
            Assert.That(lateReject.NoDrop, Is.Null);
            Assert.That(route.Rules.Any(rule =>
                rule.Action == RouteRuleAction.Route
                && ContainsIpv6Condition(rule)), Is.False);
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void RemoteRuleSetsMapSemanticTagsToDustinWinAndMetaCubeX(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreateBuilder().Plan(platform));
        SingboxRuleSet dustinWinRuleSet = route.RuleSet.Single(ruleSet =>
            ruleSet.Tag?.Contains(RuleSetTags.Ai) == true);
        Dictionary<string, string?> metaCubeXUrls = route.RuleSet
            .Where(ruleSet => ruleSet != dustinWinRuleSet)
            .ToDictionary(ruleSet => ruleSet.Tag!.Single(), ruleSet => ruleSet.Url);
        RouteRule? adBlockingRejectRule = route.Rules.SingleOrDefault(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [RuleSetTags.Ads]) == true);

        Assert.Multiple(() =>
        {
            Assert.That(route.RuleSet, Has.Count.EqualTo(4));
            Assert.That(dustinWinRuleSet.Tag, Is.EqualTo(DustinWinRuleSetTags));
            Assert.That(dustinWinRuleSet.Type, Is.EqualTo(RuleSetType.Remote));
            Assert.That(dustinWinRuleSet.Format, Is.EqualTo(RuleSetFormat.Binary));
            Assert.That(dustinWinRuleSet.Url, Is.EqualTo(DustinWinRuleSetUrlTemplate));
            Assert.That(dustinWinRuleSet.UpdateInterval, Is.EqualTo("1d"));
            Assert.That(metaCubeXUrls, Is.EquivalentTo(new Dictionary<string, string?>
            {
                [RuleSetTags.Ads] = MetaCubeXRuleSetUrlBase + "category-ads-and-telemetry.srs",
                [RuleSetTags.Google] = MetaCubeXRuleSetUrlBase + "google.srs",
                [RuleSetTags.Microsoft] = MetaCubeXRuleSetUrlBase + "microsoft.srs"
            }));
            Assert.That(route.RuleSet.All(ruleSet =>
                ruleSet.Type == RuleSetType.Remote
                && ruleSet.Format == RuleSetFormat.Binary
                && ruleSet.UpdateInterval == "1d"), Is.True);
            Assert.That(adBlockingRejectRule, Is.Not.Null);
            Assert.That(
                route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetProxy));
            Assert.That(
                route.RuleSet.All(ruleSet => ruleSet.HttpClient == null),
                Is.True);
            Assert.That(
                route.RuleSet.SelectMany(ruleSet => ruleSet.Tag ?? [])
                    .Concat(route.Rules.SelectMany(ReferencedRuleSets)),
                Does.Not.Contain("adguard-dns"));
        });
    }

    [Test]
    public void Ipv6GatePrecedesProxyServicesAndDomesticIpv4Fallback()
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreateBuilder().Plan(TargetPlatform.Linux));

        SingboxRuleSet? googleRuleSet = route.RuleSet.SingleOrDefault(ruleSet =>
            ruleSet.Tag?.Contains("google") == true);
        int adBlockingIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [RuleSetTags.Ads]) == true);
        int aiUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("ai") == true);
        int googleUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("google") == true);
        int lateIpv6RejectIndex = route.Rules.FindLastIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.IpVersion == 6);
        int aiRouteIndex = FindRouteRuleIndex(
            route,
            "ai");
        int googleRouteIndex = FindRouteRuleIndex(route, "google");
        int firstDomesticIpv4RuleIndex = FindRouteRuleIndex(route, "cn");

        Assert.Multiple(() =>
        {
            Assert.That(googleRuleSet, Is.Not.Null);
            Assert.That(googleRuleSet!.Type, Is.EqualTo(RuleSetType.Remote));
            Assert.That(googleRuleSet.Format, Is.EqualTo(RuleSetFormat.Binary));
            Assert.That(googleRuleSet.Url, Is.EqualTo(MetaCubeXRuleSetUrlBase + "google.srs"));
            Assert.That(googleRuleSet.UpdateInterval, Is.EqualTo("1d"));
            Assert.That(
                new[]
                {
                    adBlockingIndex,
                    aiUdp443RejectIndex,
                    googleUdp443RejectIndex,
                    lateIpv6RejectIndex,
                    aiRouteIndex,
                    googleRouteIndex,
                    firstDomesticIpv4RuleIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(
                route.Rules[aiRouteIndex].Outbound,
                Is.EqualTo(ServiceGroupNames.Ai));
            Assert.That(
                route.Rules[googleRouteIndex].Outbound,
                Is.EqualTo(ServiceGroupNames.Google));
        });
    }

    [Test]
    public void Udp443PolicyAllowsDomesticBeforeTheGeneralForeignReject()
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreateBuilder().Plan(TargetPlatform.Linux));

        List<(RouteRule Rule, int Index)> udp443Rejects = route.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Action == RouteRuleAction.Reject
                && ContainsUdp443Condition(item.Rule))
            .ToList();
        int domesticDomainUdpDirectIndex = FindUdp443DirectIndex(route, "cn");
        int domesticIpUdpDirectIndex = FindUdp443DirectIndex(route, "cnip");
        int foreignUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet == null);
        int firstStandardServiceIndex = FindFirstStandardServiceIndex(route);
        int domesticDomainDirectIndex = FindRouteRuleIndex(route, "cn");
        int domesticIpDirectIndex = FindRouteRuleIndex(route, "cnip");

        Assert.That(udp443Rejects, Has.Count.EqualTo(3));
        RouteRule foreignUdp443Reject = route.Rules[foreignUdp443RejectIndex];
        Assert.Multiple(() =>
        {
            Assert.That(foreignUdp443Reject.Type, Is.Null);
            Assert.That(foreignUdp443Reject.RuleSet, Is.Null);
            Assert.That(foreignUdp443Reject.Inbound, Is.EquivalentTo(new[]
            {
                SingboxTags.TunInbound
            }));
            Assert.That(foreignUdp443Reject.Port, Is.EqualTo(new[] { 443 }));
            Assert.That(foreignUdp443Reject.Network, Is.EqualTo(new[] { "udp" }));
            Assert.That(udp443Rejects.All(item => item.Rule.NoDrop == true), Is.True);
            Assert.That(
                route.Rules.Where(rule => rule.Action == RouteRuleAction.Reject
                    && !ContainsUdp443Condition(rule))
                    .All(rule => rule.NoDrop == null),
                Is.True);
            Assert.That(
                new[]
                {
                    domesticDomainUdpDirectIndex,
                    domesticIpUdpDirectIndex,
                    foreignUdp443RejectIndex,
                    firstStandardServiceIndex,
                    domesticDomainDirectIndex,
                    domesticIpDirectIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public void NoIpv6RouteCanReachDirectOrProxyOutbounds()
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreateBuilder().Plan(TargetPlatform.Linux));

        List<int> ipv6RejectIndexes = route.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Action == RouteRuleAction.Reject
                && item.Rule.IpVersion == 6)
            .Select(item => item.Index)
            .ToList();
        List<int> proxyServiceRouteIndexes = route.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Action == RouteRuleAction.Route
                && item.Rule.Outbound != null
                && BoxForgeProfile.Default.Services.Any(service =>
                    service.Name == item.Rule.Outbound))
            .Select(item => item.Index)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ipv6RejectIndexes, Has.Count.EqualTo(2));
            Assert.That(proxyServiceRouteIndexes, Is.Not.Empty);
            Assert.That(
                proxyServiceRouteIndexes,
                Is.All.GreaterThan(ipv6RejectIndexes[^1]),
                "Every proxy service route must be behind the late IPv6 gate.");
            Assert.That(
                route.Rules.Any(rule => rule.Action == RouteRuleAction.Route
                    && ContainsIpv6Condition(rule)),
                Is.False,
                "IPv6 must never be routed to an outbound.");
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void TcpSniffSkipsCommonServerFirstPortsAndKeepsNonstandardWebPorts(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreateBuilder().Plan(platform));

        List<RouteRule> sniffRules = route.Rules
            .Where(rule => rule.Action == RouteRuleAction.Sniff)
            .ToList();

        Assert.That(sniffRules, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            RouteRule tcp = sniffRules[0];
            Assert.That(tcp.Type, Is.EqualTo(RouteRuleType.Logical));
            Assert.That(tcp.Mode, Is.EqualTo(RouteLogicalMode.And));
            Assert.That(tcp.Inbound, Is.Null);
            Assert.That(tcp.Network, Is.Null);
            Assert.That(tcp.Rules, Has.Count.EqualTo(2));
            Assert.That(tcp.Rules![0].Inbound, Is.EqualTo(new[] { SingboxTags.TunInbound }));
            Assert.That(tcp.Rules[0].Network, Is.EqualTo(new[] { "tcp" }));
            Assert.That(tcp.Rules[1].Port, Is.EqualTo(new[] { 21, 22, 3306, 5900 }));
            Assert.That(tcp.Rules[1].Invert, Is.True);
            Assert.That(tcp.Sniffer, Is.EqualTo(new[] { "http", "tls" }));
            Assert.That(tcp.Timeout, Is.EqualTo("300ms"));
            AssertSniffRule(sniffRules[1], "udp", ["quic", "stun"]);
            Assert.That(
                route.Rules.Any(rule => rule.Protocol?.Contains("ssh") == true),
                Is.False,
                "SSH routing must not depend on a disabled sniffer.");
        });
    }

    [Test]
    public void StunProtocolRejectFollowsUdpSniffAndPrecedesPostSniffRouting()
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreateBuilder().Plan(TargetPlatform.Linux));

        int stunProtocolRejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.Protocol?.SequenceEqual(["stun"]) == true);
        int tcpSniffIndex = FindSniffIndex(route, "tcp");
        int udpSniffIndex = FindSniffIndex(route, "udp");
        int aiUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("ai") == true);
        int googleUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("google") == true);
        int aiRouteIndex = FindRouteRuleIndex(
            route,
            "ai");
        int googleRouteIndex = FindRouteRuleIndex(route, "google");
        int domesticDomainUdpDirectIndex = FindUdp443DirectIndex(route, "cn");
        int domesticIpUdpDirectIndex = FindUdp443DirectIndex(route, "cnip");
        int foreignUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet == null);
        int firstStandardServiceIndex = FindFirstStandardServiceIndex(route);
        int domesticDomainDirectIndex = FindRouteRuleIndex(route, "cn");
        int domesticIpDirectIndex = FindRouteRuleIndex(route, "cnip");

        Assert.That(
            new[]
            {
                tcpSniffIndex,
                udpSniffIndex,
                stunProtocolRejectIndex,
                aiUdp443RejectIndex,
                googleUdp443RejectIndex,
                aiRouteIndex,
                googleRouteIndex,
                domesticDomainUdpDirectIndex,
                domesticIpUdpDirectIndex,
                foreignUdp443RejectIndex,
                firstStandardServiceIndex,
                domesticDomainDirectIndex,
                domesticIpDirectIndex
            },
            Is.Ordered.And.All.GreaterThanOrEqualTo(0));
        RouteRule stunProtocolReject = route.Rules[stunProtocolRejectIndex];
        Assert.Multiple(() =>
        {
            Assert.That(stunProtocolRejectIndex, Is.EqualTo(udpSniffIndex + 1));
            Assert.That(route.Rules.Count(rule =>
                rule.Action == RouteRuleAction.Reject
                && rule.Protocol?.SequenceEqual(["stun"]) == true),
                Is.EqualTo(1));
            Assert.That(stunProtocolReject.Inbound, Is.EquivalentTo(new[]
            {
                SingboxTags.TunInbound
            }));
            Assert.That(stunProtocolReject.Network, Is.EqualTo(new[] { "udp" }));
            Assert.That(stunProtocolReject.Port, Is.Null);
            Assert.That(stunProtocolReject.RuleSet, Is.Null);
            Assert.That(route.Rules.Any(rule =>
                rule.Action == RouteRuleAction.Reject
                && rule.Port?.Intersect([3478, 3479, 19302, 19303]).Any() == true),
                Is.False);
        });
    }

    [TestCase("reordered")]
    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("unknown")]
    public void RulePlanRejectsBrokenStageConstraints(string mutation)
    {
        var plan = CreateBuilder().Plan(TargetPlatform.Linux);
        List<RoutePhase> stages = plan.Phases.ToList();
        switch (mutation)
        {
            case "reordered":
                int early = stages.FindIndex(stage => stage == RoutePhase.EarlyIpv6Guard);
                int direct = stages.FindIndex(stage => stage == RoutePhase.PreSniffDirect);
                (stages[early], stages[direct]) = (stages[direct], stages[early]);
                break;
            case "missing":
                stages.RemoveAt(stages.Count - 1);
                break;
            case "duplicate":
                stages.Add(stages[^1]);
                break;
            case "unknown":
                stages[^1] = (RoutePhase)(-1);
                break;
        }

        Assert.Throws<InvalidOperationException>(() => SingboxRouteEmitter.Emit(plan with { Phases = stages }));
    }

    private static void AssertSniffRule(
        RouteRule rule,
        string network,
        string[] sniffers)
    {
        Assert.Multiple(() =>
        {
            Assert.That(rule.Inbound, Is.EquivalentTo(new[]
            {
                SingboxTags.TunInbound
            }));
            Assert.That(rule.Network, Is.EqualTo(new[] { network }));
            Assert.That(rule.Sniffer, Is.EqualTo(sniffers));
            Assert.That(rule.Port, Is.Null, "UDP sniffing must cover all ports.");
            Assert.That(rule.Timeout, Is.EqualTo("300ms"));
        });
    }

    private static RouteProfileBuilder CreateBuilder() =>
        new(BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = false } }
        })!.AsObject()));

    private static List<RouteRule> DirectRules(RouteConfig route) =>
        route.Rules.Where(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound).ToList();

    private static List<RouteRule> BridgeRules(RouteConfig route) =>
        route.Rules.Where(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.BridgeOutbound).ToList();

    private static bool SupportsTunPreMatch(RouteRule rule) =>
        rule.Inbound == null
        || rule.Inbound.Contains(SingboxTags.TunInbound);

    private static bool ContainsBridgePreferenceGate(RouteRule rule) =>
        rule.PreferredBy?.SequenceEqual([SingboxTags.BridgeOutbound]) == true
        || rule.Rules?.Any(ContainsBridgePreferenceGate) == true;

    private static bool ContainsTunPreMatchGate(RouteRule rule) =>
        rule.Inbound?.SequenceEqual([SingboxTags.TunInbound]) == true
        || rule.Rules?.Any(ContainsTunPreMatchGate) == true;

    private static string MatcherKey(RouteRule rule)
    {
        return string.Join('|',
            rule.Type,
            rule.Mode,
            rule.IpVersion,
            Join(rule.Protocol),
            Join(rule.Port),
            Join(rule.Network),
            Join(rule.RuleSet),
            Join(rule.IpCidr),
            rule.IpIsPrivate,
            rule.Invert,
            string.Join(';', rule.Rules?.Select(MatcherKey) ?? []));
    }

    private static void AssertDirectLayerOrder(RouteConfig route)
    {
        int udpSniffIndex = FindSniffIndex(route, "udp");
        for (var index = 0; index < route.Rules.Count; index++)
        {
            RouteRule direct = route.Rules[index];
            if (!IsForwardingEligibleDirect(direct))
            {
                continue;
            }

            bool postUdpSniff = index > udpSniffIndex;
            RouteRule bridge = route.Rules[index - 1];
            Assert.Multiple(() =>
            {
                Assert.That(bridge.Action, Is.EqualTo(RouteRuleAction.Route));
                Assert.That(
                    bridge.Outbound,
                    Is.EqualTo(SingboxTags.BridgeOutbound));
                AssertForwardingMatcher(
                    bridge,
                    direct,
                    postUdpSniff,
                    useBridgeGate: true);
            });

            RouteRule bypass = route.Rules[index - 2];
            Assert.Multiple(() =>
            {
                Assert.That(bypass.Action, Is.EqualTo(RouteRuleAction.Bypass));
                Assert.That(bypass.Outbound, Is.Null);
                AssertForwardingMatcher(
                    bypass,
                    direct,
                    postUdpSniff,
                    useBridgeGate: false);
            });
        }
    }

    private static bool IsForwardingEligibleDirect(RouteRule rule) =>
        rule.Action == RouteRuleAction.Route
        && rule.Outbound == SingboxTags.DirectOutbound;

    private static void AssertForwardingMatcher(
        RouteRule forwarding,
        RouteRule direct,
        bool postUdpSniff,
        bool useBridgeGate)
    {
        Assert.That(direct.Type, Is.Null);
        RouteRule normalized = forwarding with
        {
            Inbound = direct.Inbound,
            Network = direct.Network,
            PreferredBy = direct.PreferredBy
        };
        Assert.That(MatcherKey(normalized), Is.EqualTo(MatcherKey(direct)));

        Assert.That(ContainsTunPreMatchGate(forwarding), Is.True);
        Assert.That(
            ContainsBridgePreferenceGate(forwarding),
            Is.EqualTo(useBridgeGate));
        Assert.That(
            ContainsNetwork(forwarding, "udp"),
            Is.EqualTo(postUdpSniff || direct.Network?.Contains("udp") == true));
    }

    private static bool ContainsNetwork(RouteRule rule, string network) =>
        rule.Network?.Contains(network) == true
        || rule.Rules?.Any(child => ContainsNetwork(child, network)) == true;

    private static List<RouteRule> PreSniffDirectRules(RouteConfig route) =>
        route.Rules.TakeWhile(rule => rule.Action != RouteRuleAction.Sniff)
            .Where(rule =>
                rule.Type == null
                && rule.Action == RouteRuleAction.Route
                && rule.Outbound == SingboxTags.DirectOutbound
                && SupportsTunPreMatch(rule))
            .ToList();

    private static int FindFirstSniffIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule => rule.Action == RouteRuleAction.Sniff);

    private static string Join<T>(IEnumerable<T>? values) =>
        values == null ? string.Empty : string.Join(',', values);

    private static int FindFirstStandardServiceIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && BoxForgeProfile.Default.Services
                .Where(service => !service.PrecedesDomesticRoutes)
                .Any(service =>
                rule.Outbound == service.Name));

    private static int FindUdp443DirectIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains(ruleSet) == true);

    private static int FindSniffIndex(RouteConfig route, string network) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Sniff
            && ContainsNetwork(rule, network));

    private static int FindRouteRuleIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && (rule.Outbound == SingboxTags.DirectOutbound
                || BoxForgeProfile.Default.Services.Any(service =>
                    service.Name == rule.Outbound))
            && !ContainsUdp443Condition(rule)
            && !ContainsIpv6Condition(rule)
            && rule.RuleSet?.Contains(ruleSet) == true);

    private static bool ContainsUdp443Condition(RouteRule rule) =>
        rule.Port?.Contains(443) == true
        && rule.Network?.Contains("udp") == true
        || rule.Rules?.Any(ContainsUdp443Condition) == true;

    private static bool ContainsIpv6Condition(RouteRule rule) =>
        rule.IpVersion == 6
        || rule.Rules?.Any(ContainsIpv6Condition) == true;

    private static IEnumerable<string> ReferencedRuleSets(RouteRule rule)
    {
        if (rule.RuleSet != null)
        {
            foreach (string ruleSet in rule.RuleSet)
            {
                yield return ruleSet;
            }
        }

        if (rule.Rules == null)
        {
            yield break;
        }

        foreach (RouteRule child in rule.Rules)
        {
            foreach (string ruleSet in ReferencedRuleSets(child))
            {
                yield return ruleSet;
            }
        }
    }
}
