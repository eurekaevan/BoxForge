using BoxForge.Exceptions;
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
        "games",
        RuleSetTags.Cn,
        RuleSetTags.CnIp
    ];

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void FirstRouteIsATunPort53FallbackForBothTcpAndUdp(TargetPlatform platform)
    {
        var plan = CreatePlan(platform);
        Assert.DoesNotThrow(plan.Validate);
        RouteRule rule = SingboxRouteEmitter.Emit(plan).Rules[0];
        Assert.Multiple(() =>
        {
            Assert.That(rule.Action, Is.EqualTo(RouteRuleAction.HijackDns));
            Assert.That(rule.Inbound, Is.EqualTo(new[] { SingboxTags.TunInbound }));
            Assert.That(rule.Port, Is.EqualTo(new[] { 53 }));
            Assert.That(rule.Network, Is.Null, "No network restriction: both TCP and UDP are covered.");
            Assert.That(rule.Protocol, Is.Null);
            Assert.That(rule.Type, Is.Null);
            Assert.That(rule.Mode, Is.Null);
            Assert.That(rule.Rules, Is.Null);
        });
    }

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
        var settings = profile.Resolve(platform).Settings;
        var plan = RouteProfileBuilder.Plan(profile.Resolve(platform));
        string expected = JsonSerializer.Serialize(SingboxRouteEmitter.Emit(RouteProfileBuilder.Plan(profile.Resolve(platform))));
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
            Assert.That(plan.TailscaleEnabled, Is.True);
            Assert.That(RouteProfileBuilder.Plan(profile.Resolve(platform)).TailscaleEnabled, Is.True);
            Assert.That(RouteProfileBuilder.Plan(changedProfile.Resolve(platform)).TailscaleEnabled, Is.False);
            Assert.That(plan.Services.Where(service => service.PrecedesDomesticRoutes).Select(service => service.Outbound),
                Is.EqualTo(profile.Services.Where(service => service.PrecedesDomesticRoutes).Select(service => service.Name)));
            Assert.That(plan.Services.Where(service => !service.PrecedesDomesticRoutes).Select(service => service.Outbound),
                Is.EqualTo(profile.Services.Where(service => !service.PrecedesDomesticRoutes).Select(service => service.Name)));
            Assert.That(plan.RuleSets.SelectMany(source => source.Tags), Is.EqualTo(profile.Resolve(platform).RuleSets.SelectMany(source => source.Tags)));
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
            rule.DefaultInterfaceAddress?.Clear();
        }
    }

    [TestCase("service_reference", "IR005")]
    [TestCase("service_priority_order", "IR010")]
    [TestCase("duplicate_service_id", "IR009")]
    [TestCase("duplicate_service_outbound", "IR009")]
    [TestCase("missing_service_id", "IR009")]
    [TestCase("missing_service_outbound", "IR009")]
    [TestCase("empty_service_scope", "IR008")]
    [TestCase("default_service_scope", "IR008")]
    [TestCase("null_service", "IR008")]
    [TestCase("duplicate_rule_set", "IR009")]
    [TestCase("missing_base_rule_set", "IR009")]
    public void InvalidRouteDeclarationsFailBeforeEmission(string fault, string code)
    {
        var plan = CreatePlan(TargetPlatform.Linux);
        ServiceRoutePlan first = plan.Services[0];
        plan = fault switch
        {
            "service_reference" => plan with { Services = plan.Services.SetItem(0, first with { RuleSets = ["missing"] }) },
            "service_priority_order" => plan with
            {
                Services = plan.Services.SetItem(0, first with { PrecedesDomesticRoutes = false })
            },
            "duplicate_service_id" => plan with { Services = plan.Services.SetItem(1, plan.Services[1] with { ServiceId = first.ServiceId }) },
            "duplicate_service_outbound" => plan with { Services = plan.Services.SetItem(1, plan.Services[1] with { Outbound = first.Outbound }) },
            "missing_service_id" => plan with { Services = plan.Services.SetItem(0, first with { ServiceId = "" }) },
            "missing_service_outbound" => plan with { Services = plan.Services.SetItem(0, first with { Outbound = "" }) },
            "empty_service_scope" => plan with { Services = plan.Services.SetItem(0, first with { RuleSets = [] }) },
            "default_service_scope" => plan with { Services = plan.Services.SetItem(0, first with { RuleSets = default }) },
            "null_service" => plan with { Services = plan.Services.SetItem(0, null!) },
            "duplicate_rule_set" => plan with { RuleSets = [.. plan.RuleSets, plan.RuleSets[0]] },
            _ => plan with
            {
                RuleSets = plan.RuleSets.Select(source => source with { Tags = [.. source.Tags.Where(tag => tag != RuleSetTags.Ads)] })
                    .Where(source => !source.Tags.IsEmpty).ToArray()
            }
        };
        var exception = Assert.Throws<ConfigValidationException>(plan.Validate)!;
        Assert.That(exception.Diagnostics.Any(item => item.Code == code && item.Path.StartsWith("plan.route.", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void EmissionExpandsSniffAndServicePriority()
    {
        RoutePlan plan = CreatePlan(TargetPlatform.Linux);
        RouteConfig route = SingboxRouteEmitter.Emit(plan);
        RouteRule[] sniff = route.Rules.Where(rule => rule.Action == RouteRuleAction.Sniff).ToArray();
        RouteRule[] direct = route.Rules.Where(rule => rule.IpIsPrivate == true).ToArray();
        var settings = BoxForgeProfile.Default.Resolve(TargetPlatform.Linux);
        Assert.Multiple(() =>
        {
            Assert.That(plan.SniffTimeout, Is.EqualTo("300ms"));
            Assert.That(plan.SniffExcludedPorts, Is.EqualTo(new[] { 21, 22, 3306, 5900 }));
            Assert.That(route.Rules.Any(rule => rule.Action == RouteRuleAction.Reject
                && rule.Rules?.Any(child => child.IpVersion == 6) == true), Is.False);
            Assert.That(sniff, Has.Length.EqualTo(2));
            Assert.That(sniff[0].Rules![0].Inbound, Is.EqualTo(new[] { SingboxTags.TunInbound }));
            Assert.That(sniff[0].Rules![0].Network, Is.EqualTo(new[] { "tcp" }));
            Assert.That(sniff[0].Rules![1].Port, Is.EqualTo(plan.SniffExcludedPorts));
            Assert.That(sniff[0].Rules![1].Invert, Is.True);
            Assert.That(sniff[0].Timeout, Is.EqualTo(plan.SniffTimeout));
            Assert.That(sniff[1].Network, Is.EqualTo(new[] { "udp" }));
            Assert.That(direct.Select(rule => rule.Action), Is.EqualTo(new RouteRuleAction?[]
            {
                RouteRuleAction.Route
            }));
            Assert.That(direct.Select(rule => rule.Outbound), Is.EqualTo(new string?[]
            {
                SingboxTags.DirectOutbound
            }));
            Assert.That(plan.Services.Where(service => service.PrecedesDomesticRoutes).Select(service => service.Outbound),
                Is.EqualTo(settings.Services.Where(service => service.PrecedesDomesticRoutes).Select(service => service.Name)));
            Assert.That(plan.Services.Where(service => !service.PrecedesDomesticRoutes).Select(service => service.Outbound),
                Is.EqualTo(settings.Services.Where(service => !service.PrecedesDomesticRoutes).Select(service => service.Name)));
        });
    }


    [TestCase(AddressFamilyPolicy.Ipv4Only)]
    [TestCase(AddressFamilyPolicy.DualStack)]
    public void OnlyIpv4OnlyAddsIpv6RejectGuards(AddressFamilyPolicy addressFamily)
    {
        RoutePlan plan = CreatePlan(TargetPlatform.Linux, addressFamily);
        List<RouteRule> rules = SingboxRouteEmitter.Emit(plan).Rules;
        Assert.That(rules.Any(rule => rule.Action == RouteRuleAction.Reject && rule.IpVersion == 6
            && rule.RuleSet is not null), Is.False);
        Assert.That(rules.Count(rule => rule.Action == RouteRuleAction.Reject && rule.IpVersion == 6),
            Is.EqualTo(addressFamily == AddressFamilyPolicy.Ipv4Only ? 1 : 0));
        Assert.That(rules[^1].IpVersion, Is.Null);
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void EveryPlatformUsesPlainDirectRulesWithoutForwardingLayers(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform));
        RouteRule[] direct = [.. route.Rules.Where(rule => rule.Outbound == SingboxTags.DirectOutbound)];
        Assert.Multiple(() =>
        {
            Assert.That(direct, Has.Length.EqualTo(3));
            Assert.That(direct.All(rule => rule.Action == RouteRuleAction.Route), Is.True);
            Assert.That(route.Rules.Any(rule => rule.Action == RouteRuleAction.Bypass), Is.False);
            Assert.That(route.Rules.Any(rule => rule.PreferredBy is not null), Is.False);
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void TunKeepsEarlyTailscaleGateWithoutMixedResolutionStages(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(RouteProfileBuilder.Plan(BoxForgeProfile.Default.Resolve(platform)));
        RouteRule gate = route.Rules.Single(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleEndpoint) == true);
        int privateDirectIndex = route.Rules.FindIndex(rule =>
            rule.IpIsPrivate == true && rule.Outbound == SingboxTags.DirectOutbound);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Inbound, Is.EqualTo(new[] { SingboxTags.TunInbound }));
            Assert.That(gate.Outbound, Is.EqualTo(SingboxTags.TailscaleEndpoint));
            Assert.That(route.Rules.FindIndex(rule => rule.IpVersion == 6 && rule.Action == RouteRuleAction.Reject),
                Is.GreaterThanOrEqualTo(0).And.LessThan(route.Rules.IndexOf(gate)), "Ipv4Only also applies to Tailscale.");
            Assert.That(route.Rules.IndexOf(gate), Is.LessThan(privateDirectIndex));
            Assert.That(route.Rules.Any(rule => rule.Action == RouteRuleAction.Resolve), Is.False);
            Assert.That(SingboxRouteEmitter.Emit(CreatePlan(platform)).Rules.Any(rule =>
                rule.PreferredBy?.Contains(SingboxTags.TailscaleEndpoint) == true), Is.False);
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void Ipv4OnlyRejectsIpv6BeforeLocalRoutesAndSniffWithoutADuplicateGuard(
        TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform, AddressFamilyPolicy.Ipv4Only));
        List<RouteRule> ipv6Rejects = route.Rules.Where(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.IpVersion == 6).ToList();
        Assert.That(ipv6Rejects, Has.Count.EqualTo(1));
        int rejectIndex = route.Rules.IndexOf(ipv6Rejects[0]);

        Assert.Multiple(() =>
        {
            Assert.That(
                new[]
                {
                    rejectIndex,
                    route.Rules.FindIndex(rule => rule.IpIsPrivate == true),
                    FindFirstSniffIndex(route)
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(ipv6Rejects[0].Type, Is.Null);
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
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform));
        SingboxRuleSet dustinWinRuleSet = route.RuleSet.Single(ruleSet =>
            ruleSet.Tag?.Contains("games") == true);
        Dictionary<string, string?> metaCubeXUrls = route.RuleSet
            .Where(ruleSet => ruleSet != dustinWinRuleSet)
            .ToDictionary(ruleSet => ruleSet.Tag!.Single(), ruleSet => ruleSet.Url);
        RouteRule? adBlockingRejectRule = route.Rules.SingleOrDefault(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [RuleSetTags.Ads]) == true);

        Assert.Multiple(() =>
        {
            Assert.That(route.RuleSet, Has.Count.EqualTo(6));
            Assert.That(dustinWinRuleSet.Tag, Is.EqualTo(DustinWinRuleSetTags));
            Assert.That(dustinWinRuleSet.Type, Is.EqualTo(RuleSetType.Remote));
            Assert.That(dustinWinRuleSet.Format, Is.EqualTo(RuleSetFormat.Binary));
            Assert.That(dustinWinRuleSet.Url, Is.EqualTo(DustinWinRuleSetUrlTemplate));
            Assert.That(dustinWinRuleSet.UpdateInterval, Is.EqualTo("1d"));
            Assert.That(metaCubeXUrls, Is.EquivalentTo(new Dictionary<string, string?>
            {
                ["ai"] = MetaCubeXRuleSetUrlBase + "category-ai-!cn.srs",
                ["spotify"] = MetaCubeXRuleSetUrlBase + "spotify.srs",
                [RuleSetTags.Ads] = MetaCubeXRuleSetUrlBase + "category-ads-and-telemetry.srs",
                ["google"] = MetaCubeXRuleSetUrlBase + "google.srs",
                ["github"] = MetaCubeXRuleSetUrlBase + "github.srs"
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
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(TargetPlatform.Linux, AddressFamilyPolicy.Ipv4Only));

        SingboxRuleSet? googleRuleSet = route.RuleSet.SingleOrDefault(ruleSet =>
            ruleSet.Tag?.Contains("google") == true);
        int adBlockingIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [RuleSetTags.Ads]) == true);
        int ipv6RejectIndex = route.Rules.FindLastIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsIpv6Condition(rule));
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
                    ipv6RejectIndex,
                    adBlockingIndex,
                    aiRouteIndex,
                    googleRouteIndex,
                    firstDomesticIpv4RuleIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(
                route.Rules[aiRouteIndex].Outbound,
                Is.EqualTo("🤖 AI"));
            Assert.That(
                route.Rules[googleRouteIndex].Outbound,
                Is.EqualTo("🔎 Google"));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void DefaultServiceAndDomesticRoutesHaveNoProtocolSpecificPriority(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform));

        int domesticDomainDirectIndex = FindRouteRuleIndex(route, "cn");
        int domesticIpDirectIndex = FindRouteRuleIndex(route, "cnip");
        RouteRule[] serviceRoutes = BoxForgeProfile.Default.Services
            .Select(service => route.Rules.Single(rule => rule.Action == RouteRuleAction.Route
                && rule.Outbound == service.Name))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(route.Rules.Any(ContainsUdp443Condition), Is.False);
            Assert.That(serviceRoutes.All(rule => rule.Port == null
                && rule.Network == null && rule.Protocol == null), Is.True,
                "Service routes must accept UDP 443 as well as other traffic.");
            Assert.That(route.Final, Is.EqualTo(SingboxTags.MainProxyGroup));
            Assert.That(BoxForgeProfile.Default.Services.All(service => service.PrecedesDomesticRoutes), Is.True);
            Assert.That(serviceRoutes.All(rule => route.Rules.IndexOf(rule) < domesticDomainDirectIndex), Is.True);
            Assert.That(
                new[]
                {
                    FindRouteRuleIndex(route, "ai"),
                    FindRouteRuleIndex(route, "google"),
                    domesticDomainDirectIndex,
                    domesticIpDirectIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public void NoIpv6RouteCanReachDirectOrProxyOutbounds()
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(TargetPlatform.Linux, AddressFamilyPolicy.Ipv4Only));

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
            Assert.That(ipv6RejectIndexes, Has.Count.EqualTo(1));
            Assert.That(proxyServiceRouteIndexes, Is.Not.Empty);
            Assert.That(
                proxyServiceRouteIndexes,
                Is.All.GreaterThan(ipv6RejectIndexes[^1]),
                "Every proxy service route must be behind the IPv6 gate.");
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
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform));

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
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(TargetPlatform.Linux));

        int stunProtocolRejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.Protocol?.SequenceEqual(["stun"]) == true);
        int tcpSniffIndex = FindSniffIndex(route, "tcp");
        int udpSniffIndex = FindSniffIndex(route, "udp");
        int aiRouteIndex = FindRouteRuleIndex(
            route,
            "ai");
        int googleRouteIndex = FindRouteRuleIndex(route, "google");
        int spotifyRouteIndex = FindRouteRuleIndex(route, "spotify");
        int gamesRouteIndex = FindRouteRuleIndex(route, "games");
        int domesticDomainDirectIndex = FindRouteRuleIndex(route, "cn");
        int domesticIpDirectIndex = FindRouteRuleIndex(route, "cnip");

        Assert.That(
            new[]
            {
                tcpSniffIndex,
                udpSniffIndex,
                stunProtocolRejectIndex,
                aiRouteIndex,
                googleRouteIndex,
                spotifyRouteIndex,
                gamesRouteIndex,
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

    [Test]
    public void RouteDiagnosticsCollectIndependentErrorsWithoutDisclosingValues()
    {
        const string hostile = "secret\nforged-path";
        var plan = CreatePlan(TargetPlatform.Linux) with
        {
            AddressFamily = (AddressFamilyPolicy)999,
            SniffTimeout = hostile,
            Services = [CreatePlan(TargetPlatform.Linux).Services[0] with { RuleSets = [hostile] }]
        };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        Assert.Multiple(() =>
        {
            Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[]
            {
                ("IR008", "plan.route.address_family"),
                ("IR008", "plan.route.sniff_timeout"),
                ("IR005", "plan.route.services[0].rule_sets[0]")
            }));
            Assert.That(exception.Message, Does.Not.Contain(hostile));
        });
    }

    [Test]
    public void DefaultRouteArraysProduceContractDiagnostics()
    {
        var plan = CreatePlan(TargetPlatform.Linux) with
        {
            RuleSets = [new(default, "unused", "1d")],
            Services = default
        };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[]
        {
            ("IR009", "plan.route.rule_sets[0].tags"),
            ("IR009", "plan.route.rule_sets"),
            ("IR008", "plan.route.services")
        }));
    }

    [TestCase("sniff_timeout", "plan.route.sniff_timeout")]
    [TestCase("sniff_ports_default", "plan.route.sniff_excluded_ports")]
    [TestCase("sniff_port_range", "plan.route.sniff_excluded_ports")]
    [TestCase("sniff_ports_duplicate", "plan.route.sniff_excluded_ports")]
    [TestCase("unknown_family", "plan.route.address_family")]
    public void RouteIntentRejectsInvalidScalarPolicies(string fault, string path)
    {
        var plan = CreatePlan(TargetPlatform.Linux);
        plan = fault switch
        {
            "sniff_timeout" => plan with { SniffTimeout = "0s" },
            "sniff_ports_default" => plan with { SniffExcludedPorts = default },
            "sniff_port_range" => plan with { SniffExcludedPorts = [0, 65536] },
            "sniff_ports_duplicate" => plan with { SniffExcludedPorts = [443, 443] },
            _ => plan with { AddressFamily = (AddressFamilyPolicy)(-1) }
        };
        var error = Assert.Throws<ConfigValidationException>(plan.Validate)!;
        Assert.That(error.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[] { ("IR008", path) }));
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

    private static RoutePlan CreatePlan(TargetPlatform platform, AddressFamilyPolicy addressFamily = AddressFamilyPolicy.DualStack) =>
        RouteProfileBuilder.Plan(BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = false } },
            dns = new { address_family = addressFamily.ToString() }
        })!.AsObject()).Resolve(platform));

    private static bool ContainsNetwork(RouteRule rule, string network) =>
        rule.Network?.Contains(network) == true
        || rule.Rules?.Any(child => ContainsNetwork(child, network)) == true;

    private static int FindFirstSniffIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule => rule.Action == RouteRuleAction.Sniff);

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
