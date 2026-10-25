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
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.Auto)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.Auto)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.Auto)]
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.DirectDualStack)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.DirectDualStack)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.DirectDualStack)]
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
            Assert.That(plan.UseDirectForwarding, Is.EqualTo(settings.UseDirectForwarding));
            Assert.That(plan.TailscaleEnabled, Is.True);
            Assert.That(RouteProfileBuilder.Plan(profile.Resolve(platform)).TailscaleEnabled, Is.True);
            Assert.That(RouteProfileBuilder.Plan(changedProfile.Resolve(platform)).TailscaleEnabled, Is.False);
            Assert.That(plan.Rules.Where(rule => rule.Phase == RoutePhase.PriorityServices).Select(rule => rule.Outbound),
                Is.EqualTo(profile.Services.Where(service => service.PrecedesDomesticRoutes).Select(service => service.Name)));
            Assert.That(plan.Rules.Where(rule => rule.Phase == RoutePhase.OtherServices).Select(rule => rule.Outbound),
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

    [TestCase("service_reference")]
    [TestCase("service_phase_order")]
    [TestCase("duplicate_service")]
    [TestCase("duplicate_rule_set")]
    [TestCase("missing_base_rule_set")]
    public void InvalidRouteReferencesDuplicatesAndOrderingFailBeforeEmission(string fault)
    {
        var plan = CreatePlan(TargetPlatform.Linux);
        plan = fault switch
        {
            "service_reference" => plan with
            {
                Rules = [.. plan.Rules.Select(rule => rule.Phase == RoutePhase.PriorityServices
                    ? rule with { RuleSet = ["missing"] } : rule)]
            },
            "service_phase_order" => plan with
            {
                Rules = [.. plan.Rules.Where(rule => rule.Phase != RoutePhase.PriorityServices),
                    .. plan.Rules.Where(rule => rule.Phase == RoutePhase.PriorityServices)]
            },
            "duplicate_service" => plan with
            {
                Rules = [.. plan.Rules.SelectMany(rule => rule.Phase == RoutePhase.PriorityServices
                    ? new[] { rule, rule } : [rule])]
            },
            "duplicate_rule_set" => plan with { RuleSets = [.. plan.RuleSets, plan.RuleSets[0]] },
            _ => plan with
            {
                RuleSets = plan.RuleSets.Select(source => source with { Tags = [.. source.Tags.Where(tag => tag != RuleSetTags.Ads)] })
                .Where(source => !source.Tags.IsEmpty).ToArray()
            }
        };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        string code = fault switch
        {
            "service_reference" => "IR005",
            "service_phase_order" => "IR010",
            _ => "IR009"
        };
        Assert.That(exception.Diagnostics.Any(item => item.Code == code && item.Path.StartsWith("plan.route.", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void PlanningDeterminesMatchersPriorityAndPlatformForwardingBeforeEmission()
    {
        RoutePlan plan = CreatePlan(TargetPlatform.Linux);
        RouteRulePlan earlyGuard = plan.Rules.Single(rule => rule.Phase == RoutePhase.EarlyIpv6Guard);
        RouteRulePlan lateGuard = plan.Rules.Single(rule => rule.Phase == RoutePhase.LateIpv6Guard);
        RouteRulePlan[] sniff = plan.Rules.Where(rule => rule.Phase == RoutePhase.Sniff).ToArray();
        RouteRulePlan[] direct = plan.Rules.Where(rule => rule.Phase == RoutePhase.PreSniffDirect).ToArray();
        var settings = BoxForgeProfile.Default.Resolve(TargetPlatform.Linux);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rules.Select(rule => rule.Phase), Is.Ordered);
            Assert.That(earlyGuard.Action, Is.EqualTo(RouteOperation.Reject));
            Assert.That(earlyGuard.Mode, Is.EqualTo(LogicalMatchMode.And));
            Assert.That(earlyGuard.Rules[0].IpVersion, Is.EqualTo(6));
            Assert.That(earlyGuard.Rules[1].DefaultInterfaceAddress, Is.EqualTo(new[] { "2000::/3" }));
            Assert.That(earlyGuard.Rules[1].Invert, Is.True);
            Assert.That(lateGuard.Action, Is.EqualTo(RouteOperation.Reject));
            Assert.That(lateGuard.Mode, Is.EqualTo(LogicalMatchMode.And));
            Assert.That(lateGuard.Rules[0].IpVersion, Is.EqualTo(6));
            Assert.That(lateGuard.Rules[1].DefaultInterfaceAddress, Is.EqualTo(new[] { "2000::/3" }));
            Assert.That(lateGuard.Rules[1].Invert, Is.True);
            Assert.That(sniff, Has.Length.EqualTo(2));
            Assert.That(sniff[0].Action, Is.EqualTo(RouteOperation.Sniff));
            Assert.That(sniff[0].Rules[0].Inbound, Is.EqualTo(new[] { SingboxTags.TunInbound }));
            Assert.That(sniff[0].Rules[0].Network, Is.EqualTo(new[] { "tcp" }));
            Assert.That(sniff[0].Rules[1].Port, Is.EqualTo(new[] { 21, 22, 3306, 5900 }));
            Assert.That(sniff[0].Rules[1].Invert, Is.True);
            Assert.That(sniff[0].Timeout, Is.EqualTo("300ms"));
            Assert.That(sniff[1].Network, Is.EqualTo(new[] { "udp" }));
            Assert.That(direct.Select(rule => rule.Action), Is.EqualTo(new RouteOperation?[]
            {
                RouteOperation.Bypass, RouteOperation.Route, RouteOperation.Route,
                RouteOperation.Bypass, RouteOperation.Route, RouteOperation.Route
            }));
            Assert.That(direct.Select(rule => rule.Outbound), Is.EqualTo(new string?[]
            {
                null, SingboxTags.BridgeOutbound, SingboxTags.DirectOutbound,
                null, SingboxTags.BridgeOutbound, SingboxTags.DirectOutbound
            }));
            Assert.That(plan.Rules.Where(rule => rule.Phase == RoutePhase.PriorityServices).Select(rule => rule.Outbound),
                Is.EqualTo(settings.Services.Where(service => service.PrecedesDomesticRoutes).Select(service => service.Name)));
            Assert.That(plan.Rules.Where(rule => rule.Phase == RoutePhase.OtherServices).Select(rule => rule.Outbound),
                Is.EqualTo(settings.Services.Where(service => !service.PrecedesDomesticRoutes).Select(service => service.Name)));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AutoIpv6GuardsRunBeforeDirectRoutesAndAfterSniffing(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform));
        RouteRule[] guards = route.Rules.Where(rule => rule.Action == RouteRuleAction.Reject
            && rule.Rules?.Any(child => child.IpVersion == 6) == true).ToArray();
        Assert.That(guards, Has.Length.EqualTo(2));
        foreach (RouteRule guard in guards)
        {
            Assert.Multiple(() =>
            {
                Assert.That(guard.Type, Is.EqualTo(RouteRuleType.Logical));
                Assert.That(guard.Mode, Is.EqualTo(RouteLogicalMode.And));
                Assert.That(guard.Rules, Has.Count.EqualTo(2));
                Assert.That(guard.Rules![0].IpVersion, Is.EqualTo(6));
                Assert.That(guard.Rules[1].DefaultInterfaceAddress, Is.EqualTo(new[] { "2000::/3" }));
                Assert.That(guard.Rules[1].Invert, Is.True);
                Assert.That(JsonSerializer.Serialize(guard), Does.Not.Contain("network_interface_address").And.Not.Contain("network_type"));
            });
        }
        int firstGuard = route.Rules.IndexOf(guards[0]);
        int lastGuard = route.Rules.IndexOf(guards[1]);
        Assert.Multiple(() =>
        {
            Assert.That(firstGuard, Is.LessThan(route.Rules.FindIndex(rule => rule.IpIsPrivate == true)));
            Assert.That(lastGuard, Is.GreaterThan(route.Rules.FindLastIndex(rule => rule.Action == RouteRuleAction.Sniff)));
            Assert.That(lastGuard, Is.LessThan(route.Rules.FindIndex(rule => rule.RuleSet?.Contains("ai") == true)));
            Assert.That(route.Rules.Any(rule => rule.IpVersion == 6 && rule.Action == RouteRuleAction.Reject), Is.False);
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void DirectDualStackKeepsDirectIpv6AndRejectsIpv6BeforeAnyProxyOutbound(TargetPlatform platform)
    {
        var profile = BoxForgeProfile.Default;
        RoutePlan plan = CreatePlan(platform, AddressFamilyPolicy.DirectDualStack);
        RouteConfig route = SingboxRouteEmitter.Emit(plan);
        string[] serviceRuleSets = [.. profile.Services.Where(service => service.PrecedesDomesticRoutes)
            .SelectMany(service => service.RuleSets)];
        int serviceGuardIndex = route.Rules.FindIndex(rule => rule.Action == RouteRuleAction.Reject
            && rule.IpVersion == 6 && rule.RuleSet is not null);
        int finalGuardIndex = route.Rules.Count - 1;
        int firstProxyRouteIndex = route.Rules.FindIndex(rule => rule.Action == RouteRuleAction.Route
            && profile.Services.Any(service => service.Name == rule.Outbound));
        int lastDirectIndex = route.Rules.FindLastIndex(rule => rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound);

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(plan.Validate);
            Assert.That(serviceGuardIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(route.Rules[serviceGuardIndex].RuleSet, Is.EqualTo(serviceRuleSets));
            Assert.That(serviceGuardIndex, Is.LessThan(firstProxyRouteIndex));
            Assert.That(route.Rules[finalGuardIndex].Action, Is.EqualTo(RouteRuleAction.Reject));
            Assert.That(route.Rules[finalGuardIndex].IpVersion, Is.EqualTo(6));
            Assert.That(route.Rules[finalGuardIndex].RuleSet, Is.Null);
            Assert.That(finalGuardIndex, Is.GreaterThan(lastDirectIndex));
            Assert.That(route.Final, Is.EqualTo(SingboxTags.MainProxyGroup));
            // Direct, bridge and bypass rules must never be narrowed to IPv4.
            Assert.That(route.Rules.Where(rule => rule.Action == RouteRuleAction.Bypass
                || rule.Outbound is SingboxTags.DirectOutbound or SingboxTags.BridgeOutbound)
                .All(rule => rule.IpVersion is null && !ContainsIpv6Condition(rule)), Is.True);
            Assert.That(route.DefaultDomainResolver!.Strategy, Is.EqualTo(DnsStrategy.Ipv4Only));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void DirectDualStackPreservesDomesticUdpPriorityWhenServiceScopesOverlap(TargetPlatform platform)
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            dns = new { address_family = "DirectDualStack" },
            route = new { services = new { patch = new[] { new { id = "games", rule_sets = new { replace = new[] { "cn" } } } } } }
        })!.AsObject());
        RoutePlan plan = RouteProfileBuilder.Plan(profile.Resolve(platform));
        Assert.DoesNotThrow(plan.Validate);
        int direct = plan.Rules.ToList().FindIndex(rule => rule.Phase == RoutePhase.DomesticUdp443 && rule.RuleSet.Contains("cn"));
        int guard = plan.Rules.ToList().FindIndex(rule => rule.Phase == RoutePhase.OtherServiceIpv6Guard && rule.RuleSet.Contains("cn"));
        int service = plan.Rules.ToList().FindIndex(rule => rule.Phase == RoutePhase.OtherServices && rule.RuleSet.Contains("cn"));
        Assert.That(direct, Is.GreaterThanOrEqualTo(0));
        Assert.That(guard, Is.GreaterThan(direct));
        Assert.That(service, Is.GreaterThan(guard));
        Assert.That(plan.Rules.Where(rule => rule.Phase == RoutePhase.ServiceIpv6Guard).All(rule => !rule.RuleSet.Contains("cn")), Is.True);
    }

    [TestCase("service_guard_removed")]
    [TestCase("service_guard_narrowed")]
    [TestCase("other_service_guard_removed")]
    [TestCase("other_service_guard_narrowed")]
    [TestCase("final_guard_removed")]
    [TestCase("final_guard_narrowed")]
    public void DirectDualStackPlanRequiresProxyIpv6Guards(string fault)
    {
        RoutePlan plan = CreatePlan(TargetPlatform.Linux, AddressFamilyPolicy.DirectDualStack);
        Assert.DoesNotThrow(plan.Validate);
        plan = fault switch
        {
            "service_guard_removed" => plan with { Rules = [.. plan.Rules.Where(rule => rule.Phase != RoutePhase.ServiceIpv6Guard)] },
            "service_guard_narrowed" => plan with
            {
                Rules = [.. plan.Rules.Select(rule => rule.Phase == RoutePhase.ServiceIpv6Guard
                    ? rule with { RuleSet = [rule.RuleSet[0]] } : rule)]
            },
            "other_service_guard_removed" => plan with { Rules = [.. plan.Rules.Where(rule => rule.Phase != RoutePhase.OtherServiceIpv6Guard)] },
            "other_service_guard_narrowed" => plan with
            {
                Rules = [.. plan.Rules.Select(rule => rule.Phase == RoutePhase.OtherServiceIpv6Guard
                    ? rule with { RuleSet = [rule.RuleSet[0]] } : rule)]
            },
            "final_guard_removed" => plan with { Rules = [.. plan.Rules.Where(rule => rule.Phase != RoutePhase.FinalIpv6Guard)] },
            _ => plan with
            {
                Rules = [.. plan.Rules.Select(rule => rule.Phase == RoutePhase.FinalIpv6Guard
                    ? rule with { Network = ["tcp"] } : rule)]
            }
        };
        var exception = Assert.Throws<ConfigValidationException>(plan.Validate)!;
        Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)), Does.Contain(("IR011", "plan.route.rules")));
    }

    [TestCase(AddressFamilyPolicy.Auto)]
    [TestCase(AddressFamilyPolicy.Ipv4Only)]
    [TestCase(AddressFamilyPolicy.DualStack)]
    public void OtherPoliciesDoNotAddProxyOnlyIpv6Guards(AddressFamilyPolicy addressFamily)
    {
        RoutePlan plan = CreatePlan(TargetPlatform.Linux, addressFamily);
        Assert.That(plan.Rules.Any(rule => rule.Phase is RoutePhase.ServiceIpv6Guard or RoutePhase.OtherServiceIpv6Guard or RoutePhase.FinalIpv6Guard), Is.False);
        RoutePlan injected = plan with { Rules = [.. plan.Rules, RoutePlan.FinalIpv6Guard()] };
        Assert.Throws<ConfigValidationException>(injected.Validate);
    }

    [Test]
    public void DirectForwardingLayersRespectExplicitPreAndPostSniffPhases()
    {
        RouteConfig android = SingboxRouteEmitter.Emit(CreatePlan(TargetPlatform.Android));
        RouteConfig windows = SingboxRouteEmitter.Emit(CreatePlan(TargetPlatform.Windows));
        RouteConfig linux = SingboxRouteEmitter.Emit(CreatePlan(TargetPlatform.Linux));

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
        RouteConfig route = SingboxRouteEmitter.Emit(RouteProfileBuilder.Plan(BoxForgeProfile.Default.Resolve(platform)));
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
            Assert.That(SingboxRouteEmitter.Emit(CreatePlan(platform)).Rules.Any(rule =>
                rule.PreferredBy?.Contains(SingboxTags.TailscaleEndpoint) == true), Is.False);
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllIpv6IsRejectedBeforeSniffWithLateFallbackPreserved(
        TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform, AddressFamilyPolicy.Ipv4Only));
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
            Assert.That(lateReject.Type, Is.Null);
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
                ["microsoft"] = MetaCubeXRuleSetUrlBase + "microsoft.srs"
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
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(TargetPlatform.Linux));

        SingboxRuleSet? googleRuleSet = route.RuleSet.SingleOrDefault(ruleSet =>
            ruleSet.Tag?.Contains("google") == true);
        int adBlockingIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [RuleSetTags.Ads]) == true);
        int lateIpv6RejectIndex = route.Rules.FindLastIndex(rule =>
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
                    adBlockingIndex,
                    lateIpv6RejectIndex,
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
    public void Udp443UsesServiceOrFinalProxyRoutesWithDomesticDirectPreserved(TargetPlatform platform)
    {
        RouteConfig route = SingboxRouteEmitter.Emit(CreatePlan(platform));

        int domesticDomainUdpDirectIndex = FindUdp443DirectIndex(route, "cn");
        int domesticIpUdpDirectIndex = FindUdp443DirectIndex(route, "cnip");
        int firstStandardServiceIndex = FindFirstStandardServiceIndex(route);
        int domesticDomainDirectIndex = FindRouteRuleIndex(route, "cn");
        int domesticIpDirectIndex = FindRouteRuleIndex(route, "cnip");
        RouteRule[] serviceRoutes = BoxForgeProfile.Default.Services
            .Select(service => route.Rules.Single(rule => rule.Action == RouteRuleAction.Route
                && rule.Outbound == service.Name))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(route.Rules.Any(rule => rule.Action == RouteRuleAction.Reject
                && ContainsUdp443Condition(rule)), Is.False);
            Assert.That(serviceRoutes.All(rule => rule.Port == null
                && rule.Network == null && rule.Protocol == null), Is.True,
                "Service routes must accept UDP 443 as well as other traffic.");
            Assert.That(route.Final, Is.EqualTo(SingboxTags.MainProxyGroup));
            Assert.That(
                new[]
                {
                    FindRouteRuleIndex(route, "ai"),
                    FindRouteRuleIndex(route, "google"),
                    domesticDomainUdpDirectIndex,
                    domesticIpUdpDirectIndex,
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
        int domesticDomainUdpDirectIndex = FindUdp443DirectIndex(route, "cn");
        int domesticIpUdpDirectIndex = FindUdp443DirectIndex(route, "cnip");
        int firstStandardServiceIndex = FindFirstStandardServiceIndex(route);
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
                domesticDomainUdpDirectIndex,
                domesticIpUdpDirectIndex,
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

    [Test]
    public void RouteDiagnosticsCollectIndependentErrorsWithoutDisclosingValues()
    {
        const string hostile = "secret\nforged-path";
        var plan = CreatePlan(TargetPlatform.Linux) with
        {
            AddressFamily = (AddressFamilyPolicy)999,
            Rules = [new(RoutePhase.DnsHijack) { Action = (RouteOperation)999, RuleSet = [hostile] }]
        };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        Assert.Multiple(() =>
        {
            Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[]
            {
                ("IR008", "plan.route.address_family"),
                ("IR008", "plan.route.rules[0].action"),
                ("IR005", "plan.route.rules[0].rule_set[0]")
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
            Rules = default
        };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[]
        {
            ("IR009", "plan.route.rule_sets[0].tags"),
            ("IR009", "plan.route.rule_sets"),
            ("IR008", "plan.route.rules")
        }));
    }

    [TestCase("reordered")]
    [TestCase("unknown")]
    [TestCase("final")]
    public void RulePlanRejectsInvalidActualStageOrder(string mutation)
    {
        var plan = CreatePlan(TargetPlatform.Linux);
        var rules = plan.Rules;
        int index = rules.Length - 1;
        if (mutation == "reordered")
        {
            int early = rules.ToList().FindIndex(rule => rule.Phase == RoutePhase.EarlyIpv6Guard);
            int direct = rules.ToList().FindIndex(rule => rule.Phase == RoutePhase.PreSniffDirect);
            rules = rules.SetItem(early, rules[direct]).SetItem(direct, rules[early]);
            index = early + 1;
        }
        else
            rules = rules.SetItem(index, rules[index] with
            {
                Phase = mutation == "unknown" ? (RoutePhase)(-1) : RoutePhase.FinalOutbound
            });

        var exception = Assert.Throws<ConfigValidationException>(() => (plan with { Rules = rules }).Validate())!;
        Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)),
            Does.Contain(("IR010", $"plan.route.rules[{index}].phase")));
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

    private static RoutePlan CreatePlan(TargetPlatform platform, AddressFamilyPolicy addressFamily = AddressFamilyPolicy.Auto) =>
        RouteProfileBuilder.Plan(BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = false } },
            dns = new { address_family = addressFamily.ToString() }
        })!.AsObject()).Resolve(platform));

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
