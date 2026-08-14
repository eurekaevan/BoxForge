using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class RouteProfileBuilderTests
{
    [Test]
    public void AdBlockingRuleSetsUseFixedRemoteBinaryUrlsAndAreRejected()
    {
        RouteConfig route = CreateBuilder().Build();
        var expectedRuleSets = new Dictionary<string, string>
        {
            [AdBlockingRuleSets.AntiAdTag] = AdBlockingRuleSets.AntiAdUrl,
            [AdBlockingRuleSets.SagerAdsTag] = AdBlockingRuleSets.SagerAdsUrl
        };

        List<SingboxRuleSet> adBlockingRuleSets = route.RuleSet
            .Where(ruleSet => ruleSet.Tag != null
                && expectedRuleSets.ContainsKey(ruleSet.Tag))
            .ToList();
        RouteRule? adBlockingRejectRule = route.Rules.SingleOrDefault(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [
                    AdBlockingRuleSets.AntiAdTag,
                    AdBlockingRuleSets.SagerAdsTag
                ]) == true);

        Assert.Multiple(() =>
        {
            Assert.That(adBlockingRuleSets, Has.Count.EqualTo(2));
            Assert.That(
                adBlockingRuleSets.All(ruleSet =>
                    ruleSet.Type == RuleSetType.Remote
                    && ruleSet.Format == RuleSetFormat.Binary
                    && ruleSet.Tag != null
                    && ruleSet.Url == expectedRuleSets[ruleSet.Tag]
                    && ruleSet.UpdateInterval == "1d"),
                Is.True);
            Assert.That(adBlockingRejectRule, Is.Not.Null);
            Assert.That(
                route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetDirect));
            Assert.That(
                route.RuleSet.All(ruleSet => ruleSet.HttpClient == null),
                Is.True);
            Assert.That(
                route.RuleSet.Select(ruleSet => ruleSet.Tag)
                    .Concat(route.Rules.SelectMany(ReferencedRuleSets)),
                Does.Not.Contain("adguard-dns"));
        });
    }

    [Test]
    public void Udp443IsRejectedOnlyAfterDomesticDirectDestinations()
    {
        RouteConfig route = CreateBuilder().Build();

        List<(RouteRule Rule, int Index)> udp443Rejects = route.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Action == RouteRuleAction.Reject
                && ContainsUdp443Condition(item.Rule))
            .ToList();
        int geositeUdpDirectIndex = FindUdp443DirectIndex(route, "geosite-cn");
        int udpResolveIndex = FindUdp443ResolveIndex(route);
        int geoipUdpDirectIndex = FindUdp443DirectIndex(route, "geoip-cn");
        int firstServiceIndex = FindFirstServiceIndex(route);
        int geositeDirectIndex = FindRouteRuleIndex(route, "geosite-cn");
        int resolveIndex = FindGeneralResolveIndex(route);
        int geoipDirectIndex = FindRouteRuleIndex(route, "geoip-cn");

        Assert.That(udp443Rejects, Has.Count.EqualTo(1));
        (RouteRule udp443Reject, int udp443RejectIndex) = udp443Rejects.Single();
        Assert.Multiple(() =>
        {
            Assert.That(udp443Reject.Type, Is.Null);
            Assert.That(udp443Reject.RuleSet, Is.Null);
            Assert.That(udp443Reject.Inbound, Is.EquivalentTo(new[]
            {
                SingboxTags.TunInbound,
                SingboxTags.MixedInbound
            }));
            Assert.That(udp443Reject.Port, Is.EqualTo(new[] { 443 }));
            Assert.That(udp443Reject.Network, Is.EqualTo(new[] { "udp" }));
            Assert.That(
                new[]
                {
                    geositeUdpDirectIndex,
                    udpResolveIndex,
                    geoipUdpDirectIndex,
                    udp443RejectIndex,
                    firstServiceIndex,
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
        RouteConfig route = CreateBuilder().Build();

        int domesticIpv6DirectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && rule.Outbound == new SingboxOptions().Direct
            && ContainsIpv6Condition(rule)
            && ReferencedRuleSets(rule).ToHashSet().SetEquals(new[]
            {
                "geosite-cn",
                "geosite-category-pt",
                "geoip-cn"
            }));
        int publicIpv6RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.IpCidr?.Contains("::/0") == true);
        int firstServiceIndex = FindFirstServiceIndex(route);

        Assert.Multiple(() =>
        {
            Assert.That(
                new[]
                {
                    domesticIpv6DirectIndex,
                    publicIpv6RejectIndex,
                    firstServiceIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
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
    public void SniffingUsesOnlyWebAndQuicAcrossAllPorts()
    {
        RouteConfig route = CreateBuilder().Build();

        List<RouteRule> sniffRules = route.Rules
            .Where(rule => rule.Action == RouteRuleAction.Sniff)
            .ToList();

        Assert.That(sniffRules, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            AssertSniffRule(sniffRules[0], "tcp", ["http", "tls"]);
            AssertSniffRule(sniffRules[1], "udp", ["quic"]);
            Assert.That(
                route.Rules.Any(rule => rule.Protocol?.Contains("ssh") == true),
                Is.False,
                "SSH routing must not depend on a disabled sniffer.");
        });
    }

    [Test]
    public void FixedStunRejectPrecedesSniffAndForeignUdp443Policy()
    {
        RouteConfig route = CreateBuilder().Build();

        int stunRejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.Network?.Contains("udp") == true
            && rule.Port?.Contains(3478) == true);
        int tcpSniffIndex = FindSniffIndex(route, "tcp");
        int udpSniffIndex = FindSniffIndex(route, "udp");
        int geositeUdpDirectIndex = FindUdp443DirectIndex(route, "geosite-cn");
        int udpResolveIndex = FindUdp443ResolveIndex(route);
        int geoipUdpDirectIndex = FindUdp443DirectIndex(route, "geoip-cn");
        int udp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule));
        int firstServiceIndex = FindFirstServiceIndex(route);
        int geositeDirectIndex = FindRouteRuleIndex(route, "geosite-cn");
        int resolveIndex = FindGeneralResolveIndex(route);
        int geoipDirectIndex = FindRouteRuleIndex(route, "geoip-cn");

        Assert.That(
            new[]
            {
                stunRejectIndex,
                tcpSniffIndex,
                udpSniffIndex,
                geositeUdpDirectIndex,
                udpResolveIndex,
                geoipUdpDirectIndex,
                udp443RejectIndex,
                firstServiceIndex,
                geositeDirectIndex,
                resolveIndex,
                geoipDirectIndex
            },
            Is.Ordered.And.All.GreaterThanOrEqualTo(0));
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
            Options.Create(new SingboxOptions()),
            Options.Create(new TailscaleOptions()));

    private static int FindFirstServiceIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && ProfileDefinitions.Services.Any(service =>
                rule.Outbound == service.Name));

    private static int FindUdp443DirectIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains(ruleSet) == true);

    private static int FindUdp443ResolveIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Resolve
            && ContainsUdp443Condition(rule));

    private static int FindGeneralResolveIndex(RouteConfig route) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Resolve
            && !ContainsUdp443Condition(rule));

    private static int FindSniffIndex(RouteConfig route, string network) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Sniff
            && rule.Network?.Contains(network) == true);

    private static int FindRouteRuleIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && !ContainsUdp443Condition(rule)
            && rule.RuleSet?.Contains(ruleSet) == true);

    private static bool ContainsUdp443Condition(RouteRule rule) =>
        rule.Port?.Contains(443) == true
        && rule.Network?.Contains("udp") == true
        || rule.Rules?.Any(ContainsUdp443Condition) == true;

    private static bool ContainsIpv6Condition(RouteRule rule) =>
        rule.IpCidr?.Contains("::/0") == true
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
