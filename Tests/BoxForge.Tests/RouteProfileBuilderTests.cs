using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class RouteProfileBuilderTests
{
    private const string GeositeRuleSetUrlTemplate =
        "https://fastly.jsdelivr.net/gh/SagerNet/sing-geosite@rule-set/{tag}.srs";
    private const string GeoIpRuleSetUrl =
        "https://fastly.jsdelivr.net/gh/SagerNet/sing-geoip@rule-set/geoip-cn.srs";
    private static readonly string[] GeositeRuleSetTags =
    [
        AdBlockingRuleSets.SagerAdsTag,
        "geosite-category-pt",
        "geosite-google",
        "geosite-cn",
        "geosite-spotify",
        "geosite-steam",
        "geosite-category-ai-!cn",
        "geosite-microsoft"
    ];

    [Test]
    public void DirectForwardingLayersRespectExplicitPreAndPostSniffPhases()
    {
        RouteConfig android = CreateBuilder().Build(TargetPlatform.Android);
        RouteConfig windows = CreateBuilder().Build(TargetPlatform.Windows);
        RouteConfig linux = CreateBuilder().Build(TargetPlatform.Linux);

        List<RouteRule> androidDirect = DirectRules(android);
        List<RouteRule> preSniffDirect = PreSniffDirectRules(android);
        List<RouteRule> windowsBridge = BridgeRules(windows);
        List<RouteRule> linuxBridge = BridgeRules(linux);
        List<RouteRule> linuxBypass = linux.Rules
            .Where(rule => rule.Action == RouteRuleAction.Bypass)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(androidDirect, Has.Count.EqualTo(8));
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
            Assert.That(windowsBridge, Has.Count.EqualTo(7));
            Assert.That(linuxBridge, Has.Count.EqualTo(7));
            Assert.That(linuxBypass, Has.Count.EqualTo(7));
            Assert.That(
                windows.Rules.Any(rule => rule.Action == RouteRuleAction.Bypass),
                Is.False);
            Assert.That(
                android.Rules.Any(rule =>
                    rule.Action == RouteRuleAction.Bypass
                    || rule.Outbound == SingboxTags.BridgeOutbound),
                Is.False);
            Assert.That(
                windowsBridge.All(ContainsBridgePreferenceGate),
                Is.True);
            Assert.That(
                linuxBridge.All(ContainsBridgePreferenceGate),
                Is.True);
            Assert.That(
                windowsBridge.All(ContainsTunPreMatchGate),
                Is.True);
            Assert.That(
                linuxBridge.All(ContainsTunPreMatchGate),
                Is.True);
            Assert.That(
                linuxBypass.All(ContainsTunPreMatchGate),
                Is.True);
            Assert.That(linuxBypass.All(rule => rule.Outbound == null), Is.True);
            Assert.That(
                windowsBridge.Count(rule => windows.Rules.IndexOf(rule)
                    < FindFirstSniffIndex(windows)),
                Is.EqualTo(2));
            Assert.That(
                windowsBridge.Count(rule => windows.Rules.IndexOf(rule)
                    > FindSniffIndex(windows, "udp")),
                Is.EqualTo(5));
            Assert.That(
                linuxBypass.Count(rule => linux.Rules.IndexOf(rule)
                    > FindSniffIndex(linux, "udp")),
                Is.EqualTo(5));
            Assert.That(
                windowsBridge.Where(rule => windows.Rules.IndexOf(rule)
                    > FindSniffIndex(windows, "udp"))
                    .All(rule => ContainsNetwork(rule, "udp")),
                Is.True);
        });

        AssertDirectLayerOrder(windows, includeBypass: false);
        AssertDirectLayerOrder(linux, includeBypass: true);
    }

    [Test]
    public void MixedOnlyPrivateFallbackRemainsOnDirect()
    {
        RouteConfig linux = CreateBuilder().Build(TargetPlatform.Linux);

        RouteRule mixedPrivate = linux.Rules.Single(rule =>
            rule.Inbound?.SequenceEqual([SingboxTags.MixedInbound]) == true
            && rule.IpIsPrivate == true
            && rule.Action == RouteRuleAction.Route);

        Assert.Multiple(() =>
        {
            Assert.That(mixedPrivate.Outbound, Is.EqualTo(SingboxTags.DirectOutbound));
            Assert.That(
                linux.Rules.Any(rule =>
                    rule.Inbound?.SequenceEqual([SingboxTags.MixedInbound]) == true
                    && (rule.Action == RouteRuleAction.Bypass
                        || rule.Outbound == SingboxTags.BridgeOutbound)),
                Is.False);
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void ForeignIpv6IsRejectedForTunBeforeSniffWithLateFallbackPreserved(
        TargetPlatform platform)
    {
        RouteConfig route = CreateBuilder().Build(platform);
        RouteRule earlyReject = route.Rules.Single(rule =>
            rule.Type == RouteRuleType.Logical
            && rule.Action == RouteRuleAction.Reject
            && rule.Rules?.Any(child => child.Invert == true) == true);
        IReadOnlyList<RouteRule> earlyMatchers = earlyReject.Rules!;
        int earlyRejectIndex = route.Rules.IndexOf(earlyReject);
        int bootstrapDirectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound
            && rule.IpCidr?.SequenceEqual(["223.5.5.5/32"]) == true);
        RouteRule lateReject = route.Rules.Single(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.IpVersion == 6);
        int lateRejectIndex = route.Rules.IndexOf(lateReject);

        Assert.Multiple(() =>
        {
            Assert.That(
                new[]
                {
                    bootstrapDirectIndex,
                    earlyRejectIndex,
                    FindFirstSniffIndex(route),
                    lateRejectIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(earlyReject.Mode, Is.EqualTo(RouteLogicalMode.And));
            Assert.That(earlyReject.NoDrop, Is.Null);
            Assert.That(earlyMatchers, Has.Count.EqualTo(3));
            Assert.That(
                earlyMatchers.Any(child =>
                    child.Inbound?.SequenceEqual([SingboxTags.TunInbound]) == true),
                Is.True);
            Assert.That(
                earlyMatchers.Any(child => child.IpVersion == 6),
                Is.True);
            Assert.That(
                earlyMatchers.Any(child =>
                    child.RuleSet?.SequenceEqual(["geoip-cn"]) == true
                    && child.Invert == true),
                Is.True);
            Assert.That(lateReject.Type, Is.Null);
            Assert.That(lateReject.NoDrop, Is.Null);
        });
    }

    [Test]
    public void SagerNetRuleSetsGroupGeositeTagsAndRejectAds()
    {
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);
        SingboxRuleSet geositeRuleSet = route.RuleSet.Single(ruleSet =>
            ruleSet.Tag?.Contains(AdBlockingRuleSets.SagerAdsTag) == true);
        SingboxRuleSet geoIpRuleSet = route.RuleSet.Single(ruleSet =>
            ruleSet.Tag?.SequenceEqual(["geoip-cn"]) == true);
        RouteRule? adBlockingRejectRule = route.Rules.SingleOrDefault(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [AdBlockingRuleSets.SagerAdsTag]) == true);

        Assert.Multiple(() =>
        {
            Assert.That(route.RuleSet, Has.Count.EqualTo(2));
            Assert.That(geositeRuleSet.Tag, Is.EqualTo(GeositeRuleSetTags));
            Assert.That(geositeRuleSet.Type, Is.EqualTo(RuleSetType.Remote));
            Assert.That(geositeRuleSet.Format, Is.EqualTo(RuleSetFormat.Binary));
            Assert.That(geositeRuleSet.Url, Is.EqualTo(GeositeRuleSetUrlTemplate));
            Assert.That(geositeRuleSet.UpdateInterval, Is.EqualTo("1d"));
            Assert.That(geoIpRuleSet.Url, Is.EqualTo(GeoIpRuleSetUrl));
            Assert.That(adBlockingRejectRule, Is.Not.Null);
            Assert.That(
                route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetDirect));
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
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);

        SingboxRuleSet? googleRuleSet = route.RuleSet.SingleOrDefault(ruleSet =>
            ruleSet.Tag?.Contains("geosite-google") == true);
        int adBlockingIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [AdBlockingRuleSets.SagerAdsTag]) == true);
        int aiUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("geosite-category-ai-!cn") == true);
        int googleUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("geosite-google") == true);
        int serviceResolveIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Resolve
            && rule.Strategy == DnsStrategy.Ipv4Only
            && rule.RuleSet?.Contains("geosite-google") == true);
        int domesticResolveIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Resolve
            && rule.Strategy == DnsStrategy.PreferIpv4
            && rule.RuleSet?.Contains("geosite-cn") == true);
        int domesticIpv6DirectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound
            && ContainsIpv6Condition(rule));
        int publicIpv6RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.IpVersion == 6);
        int aiRouteIndex = FindRouteRuleIndex(
            route,
            "geosite-category-ai-!cn");
        int googleRouteIndex = FindRouteRuleIndex(route, "geosite-google");
        int firstDomesticIpv4RuleIndex = FindRouteRuleIndex(route, "geosite-cn");

        Assert.Multiple(() =>
        {
            Assert.That(googleRuleSet, Is.Not.Null);
            Assert.That(googleRuleSet!.Type, Is.EqualTo(RuleSetType.Remote));
            Assert.That(googleRuleSet.Format, Is.EqualTo(RuleSetFormat.Binary));
            Assert.That(googleRuleSet.Url, Is.EqualTo(GeositeRuleSetUrlTemplate));
            Assert.That(googleRuleSet.UpdateInterval, Is.EqualTo("1d"));
            Assert.That(
                new[]
                {
                    adBlockingIndex,
                    serviceResolveIndex,
                    domesticResolveIndex,
                    aiUdp443RejectIndex,
                    googleUdp443RejectIndex,
                    domesticIpv6DirectIndex,
                    publicIpv6RejectIndex,
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
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);

        List<(RouteRule Rule, int Index)> udp443Rejects = route.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Action == RouteRuleAction.Reject
                && ContainsUdp443Condition(item.Rule))
            .ToList();
        int geositeUdpDirectIndex = FindUdp443DirectIndex(route, "geosite-cn");
        int udpResolveIndex = FindUdp443ResolveIndex(route);
        int geoipUdpDirectIndex = FindUdp443DirectIndex(route, "geoip-cn");
        int foreignUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet == null);
        int firstStandardServiceIndex = FindFirstStandardServiceIndex(route);
        int geositeDirectIndex = FindRouteRuleIndex(route, "geosite-cn");
        int resolveIndex = FindGeneralResolveIndex(route);
        int geoipDirectIndex = FindRouteRuleIndex(route, "geoip-cn");

        Assert.That(udp443Rejects, Has.Count.EqualTo(3));
        RouteRule foreignUdp443Reject = route.Rules[foreignUdp443RejectIndex];
        Assert.Multiple(() =>
        {
            Assert.That(foreignUdp443Reject.Type, Is.Null);
            Assert.That(foreignUdp443Reject.RuleSet, Is.Null);
            Assert.That(foreignUdp443Reject.Inbound, Is.EquivalentTo(new[]
            {
                SingboxTags.TunInbound,
                SingboxTags.MixedInbound
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
                    geositeUdpDirectIndex,
                    udpResolveIndex,
                    geoipUdpDirectIndex,
                    foreignUdp443RejectIndex,
                    firstStandardServiceIndex,
                    geositeDirectIndex,
                    resolveIndex,
                    geoipDirectIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public void DomesticIpv6IsDirectBeforeOtherPublicIpv6IsRejected()
    {
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);

        int domesticIpv6DirectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound
            && ContainsIpv6Condition(rule)
            && ReferencedRuleSets(rule).SequenceEqual(["geoip-cn"]));
        int publicIpv6RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.IpVersion == 6);
        List<int> proxyServiceRouteIndexes = route.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Action == RouteRuleAction.Route
                && item.Rule.Outbound != null
                && ProfileDefinitions.Services.Any(service =>
                    service.Name == item.Rule.Outbound))
            .Select(item => item.Index)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(
                new[] { domesticIpv6DirectIndex, publicIpv6RejectIndex },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(proxyServiceRouteIndexes, Is.Not.Empty);
            Assert.That(
                proxyServiceRouteIndexes,
                Is.All.GreaterThan(publicIpv6RejectIndex),
                "Every proxy service route must be behind the public IPv6 gate.");
            Assert.That(
                route.Rules[domesticIpv6DirectIndex].Type,
                Is.EqualTo(RouteRuleType.Logical));
            Assert.That(
                route.Rules[domesticIpv6DirectIndex].Mode,
                Is.EqualTo(RouteLogicalMode.And));
            Assert.That(
                route.Rules.Any(rule => ContainsIpv6Condition(rule)
                    && ContainsUdp443Condition(rule)),
                Is.False,
                "Domestic IPv6 UDP/443 must be routed directly, not rejected.");
        });
    }

    [Test]
    public void MixedInboundResolvesProxyDomainsAsIpv4BeforeRouting()
    {
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);
        string[] expectedProxyRuleSets =
        [
            .. ProfileDefinitions.Services
                .SelectMany(service => service.RuleSets)
                .Distinct(StringComparer.Ordinal)
        ];

        RouteRule serviceResolve = route.Rules.Single(rule =>
            rule.Action == RouteRuleAction.Resolve
            && rule.Strategy == DnsStrategy.Ipv4Only
            && rule.RuleSet?.SequenceEqual(expectedProxyRuleSets) == true);
        RouteRule domesticResolve = route.Rules.Single(rule =>
            rule.Action == RouteRuleAction.Resolve
            && rule.Strategy == DnsStrategy.PreferIpv4
            && rule.RuleSet?.SequenceEqual(
                ["geosite-cn", "geosite-category-pt"]) == true);
        RouteRule generalResolve = route.Rules.Single(rule =>
            rule.Action == RouteRuleAction.Resolve
            && !ContainsUdp443Condition(rule)
            && rule.RuleSet == null);

        Assert.Multiple(() =>
        {
            Assert.That(serviceResolve.Inbound, Is.EqualTo(
                new[] { SingboxTags.MixedInbound }));
            Assert.That(domesticResolve.Inbound, Is.EqualTo(
                new[] { SingboxTags.MixedInbound }));
            Assert.That(generalResolve.Inbound, Is.EqualTo(
                new[] { SingboxTags.MixedInbound }));
            Assert.That(generalResolve.Strategy, Is.EqualTo(DnsStrategy.Ipv4Only));
        });
    }

    [Test]
    public void MixedInboundRoutesPrivateAddressesDirectlyAfterGeneralResolution()
    {
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);

        int earlyPrivateDirectIndex = route.Rules.FindIndex(rule =>
            rule.Inbound == null
            && rule.IpIsPrivate == true
            && rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound);
        int generalResolveIndex = FindGeneralResolveIndex(route);
        int mixedPrivateDirectIndex = route.Rules.FindIndex(rule =>
            rule.Inbound?.SequenceEqual([SingboxTags.MixedInbound]) == true
            && rule.IpIsPrivate == true
            && rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound);
        int geoipDirectIndex = FindRouteRuleIndex(route, "geoip-cn");

        Assert.That(
            new[]
            {
                earlyPrivateDirectIndex,
                generalResolveIndex,
                mixedPrivateDirectIndex,
                geoipDirectIndex
            },
            Is.Ordered.And.All.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void SniffingUsesWebQuicAndStunAcrossAllPorts()
    {
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);

        List<RouteRule> sniffRules = route.Rules
            .Where(rule => rule.Action == RouteRuleAction.Sniff)
            .ToList();

        Assert.That(sniffRules, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            AssertSniffRule(sniffRules[0], "tcp", ["http", "tls"]);
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
        RouteConfig route = CreateBuilder().Build(TargetPlatform.Linux);

        int stunProtocolRejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.Protocol?.SequenceEqual(["stun"]) == true);
        int tcpSniffIndex = FindSniffIndex(route, "tcp");
        int udpSniffIndex = FindSniffIndex(route, "udp");
        int aiUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("geosite-category-ai-!cn") == true);
        int googleUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains("geosite-google") == true);
        int aiRouteIndex = FindRouteRuleIndex(
            route,
            "geosite-category-ai-!cn");
        int googleRouteIndex = FindRouteRuleIndex(route, "geosite-google");
        int geositeUdpDirectIndex = FindUdp443DirectIndex(route, "geosite-cn");
        int udpResolveIndex = FindUdp443ResolveIndex(route);
        int geoipUdpDirectIndex = FindUdp443DirectIndex(route, "geoip-cn");
        int foreignUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && rule.RuleSet == null);
        int firstStandardServiceIndex = FindFirstStandardServiceIndex(route);
        int geositeDirectIndex = FindRouteRuleIndex(route, "geosite-cn");
        int resolveIndex = FindGeneralResolveIndex(route);
        int geoipDirectIndex = FindRouteRuleIndex(route, "geoip-cn");

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
                geositeUdpDirectIndex,
                udpResolveIndex,
                geoipUdpDirectIndex,
                foreignUdp443RejectIndex,
                firstStandardServiceIndex,
                geositeDirectIndex,
                resolveIndex,
                geoipDirectIndex
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
                SingboxTags.TunInbound,
                SingboxTags.MixedInbound
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

    private static void AssertSniffRule(
        RouteRule rule,
        string network,
        string[] sniffers)
    {
        Assert.Multiple(() =>
        {
            Assert.That(rule.Inbound, Is.EquivalentTo(new[]
            {
                SingboxTags.TunInbound,
                SingboxTags.MixedInbound
            }));
            Assert.That(rule.Network, Is.EqualTo(new[] { network }));
            Assert.That(rule.Sniffer, Is.EqualTo(sniffers));
            Assert.That(rule.Port, Is.Null, "Selected sniffers must cover all ports.");
            Assert.That(rule.Timeout, Is.EqualTo("300ms"));
        });
    }

    private static RouteProfileBuilder CreateBuilder() =>
        new(
            Options.Create(new TailscaleOptions()));

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

    private static void AssertDirectLayerOrder(
        RouteConfig route,
        bool includeBypass)
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

            if (!includeBypass)
            {
                continue;
            }

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
        && rule.Outbound == SingboxTags.DirectOutbound
        && rule.Inbound?.SequenceEqual([SingboxTags.MixedInbound]) != true;

    private static void AssertForwardingMatcher(
        RouteRule forwarding,
        RouteRule direct,
        bool postUdpSniff,
        bool useBridgeGate)
    {
        if (direct.Type != RouteRuleType.Logical)
        {
            RouteRule normalized = forwarding with
            {
                Inbound = direct.Inbound,
                Network = direct.Network,
                PreferredBy = direct.PreferredBy
            };
            Assert.That(MatcherKey(normalized), Is.EqualTo(MatcherKey(direct)));
        }
        else
        {
            IReadOnlyList<RouteRule> forwardingRules = forwarding.Rules!;
            IReadOnlyList<RouteRule> directRules = direct.Rules!;
            Assert.That(forwarding.Type, Is.EqualTo(RouteRuleType.Logical));
            Assert.That(forwarding.Mode, Is.EqualTo(RouteLogicalMode.And));
            Assert.That(
                forwardingRules.Take(directRules.Count).Select(MatcherKey),
                Is.EqualTo(directRules.Select(MatcherKey)));
            Assert.That(
                forwardingRules.Count,
                Is.EqualTo(directRules.Count + (useBridgeGate ? 3 : 2)));
        }

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
            && ProfileDefinitions.Services
                .Where(service => !service.PrecedesDomesticRoutes)
                .Any(service =>
                rule.Outbound == service.Name));

    private static int FindUdp443DirectIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == SingboxTags.DirectOutbound
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains(ruleSet) == true);

    private static int FindUdp443ResolveIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Resolve
            && ContainsUdp443Condition(rule));

    private static int FindGeneralResolveIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Resolve
            && !ContainsUdp443Condition(rule)
            && rule.RuleSet == null);

    private static int FindSniffIndex(RouteConfig route, string network) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Sniff
            && rule.Network?.Contains(network) == true);

    private static int FindRouteRuleIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && (rule.Outbound == SingboxTags.DirectOutbound
                || ProfileDefinitions.Services.Any(service =>
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
