using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class RouteProfileBuilderTests
{
    private const string AdGuardDnsRuleSetUrl =
        "https://sublinks.skuld.workers.dev/rules/adguard-dns.srs";

    [Test]
    public void AdGuardRuleSetUsesConfiguredRemoteBinaryUrlAndIsRejected()
    {
        RouteConfig route = CreateBuilder().Build();

        SingboxRuleSet? adGuardRuleSet = route.RuleSet.SingleOrDefault(ruleSet =>
            ruleSet.Tag == SingboxOptions.AdGuardDnsRuleSetTag);
        RouteRule? adGuardRejectRule = route.Rules.SingleOrDefault(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.RuleSet?.SequenceEqual(
                [SingboxOptions.AdGuardDnsRuleSetTag]) == true);

        Assert.Multiple(() =>
        {
            Assert.That(adGuardRuleSet, Is.Not.Null);
            Assert.That(adGuardRuleSet!.Type, Is.EqualTo(RuleSetType.Remote));
            Assert.That(adGuardRuleSet.Format, Is.EqualTo(RuleSetFormat.Binary));
            Assert.That(adGuardRuleSet.Url, Is.EqualTo(AdGuardDnsRuleSetUrl));
            Assert.That(
                adGuardRuleSet.HttpClient,
                Is.EqualTo(HttpClientTags.RuleSetProxy));
            Assert.That(adGuardRuleSet.UpdateInterval, Is.EqualTo("1d"));
            Assert.That(adGuardRejectRule, Is.Not.Null);
            Assert.That(
                route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetDirect));
            Assert.That(
                route.RuleSet.Where(ruleSet =>
                    ruleSet.Tag != SingboxOptions.AdGuardDnsRuleSetTag)
                    .All(ruleSet => ruleSet.HttpClient == null),
                Is.True);
            Assert.That(
                route.RuleSet.Select(ruleSet => ruleSet.Tag)
                    .Concat(route.Rules.SelectMany(ReferencedRuleSets)),
                Does.Not.Contain("geosite-category-ads-all"));
        });
    }

    [Test]
    public void Udp443IsRejectedOnlyForDomesticDirectDestinations()
    {
        RouteConfig route = CreateBuilder().Build();

        List<(RouteRule Rule, int Index)> udp443Rejects = route.Rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule.Action == RouteRuleAction.Reject
                && ContainsUdp443Condition(item.Rule))
            .ToList();

        Assert.That(udp443Rejects, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(
                udp443Rejects
                    .SelectMany(item => ReferencedRuleSets(item.Rule))
                    .Distinct(),
                Is.EquivalentTo(new[]
                {
                    "geosite-cn",
                    "geosite-category-pt",
                    "geoip-cn"
                }));
            Assert.That(
                udp443Rejects.All(item => item.Rule is
                {
                    Type: RouteRuleType.Logical,
                    Mode: RouteLogicalMode.And
                }),
                Is.True);
            Assert.That(
                route.Rules.Any(rule => rule.Action == RouteRuleAction.Reject
                    && rule.Port?.Contains(443) == true),
                Is.False,
                "UDP/443 must not be rejected by an unscoped top-level rule.");
        });
    }

    [Test]
    public void DomesticIpv6IsDirectBeforeOtherPublicIpv6IsRejected()
    {
        RouteConfig route = CreateBuilder().Build();

        int domesticUdp443RejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && ContainsIpv6Condition(rule)
            && ReferencedRuleSets(rule).ToHashSet().SetEquals(new[]
            {
                "geosite-cn",
                "geosite-category-pt",
                "geoip-cn"
            }));
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
        int firstServiceIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
            && ProfileDefinitions.Services.Any(service =>
                rule.Outbound == service.Name));

        Assert.Multiple(() =>
        {
            Assert.That(
                new[]
                {
                    domesticUdp443RejectIndex,
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
    public void FixedStunRejectPrecedesSniffAndDomesticUdp443Policy()
    {
        RouteConfig route = CreateBuilder().Build();

        int stunRejectIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && rule.Network?.Contains("udp") == true
            && rule.Port?.Contains(3478) == true);
        int tcpSniffIndex = FindSniffIndex(route, "tcp");
        int udpSniffIndex = FindSniffIndex(route, "udp");
        int geositeRejectIndex = FindUdp443RejectIndex(route, "geosite-cn");
        int geositeDirectIndex = FindRouteRuleIndex(route, "geosite-cn");
        int resolveIndex = route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Resolve);
        int geoipRejectIndex = FindUdp443RejectIndex(route, "geoip-cn");
        int geoipDirectIndex = FindRouteRuleIndex(route, "geoip-cn");

        Assert.That(
            new[]
            {
                stunRejectIndex,
                tcpSniffIndex,
                udpSniffIndex,
                geositeRejectIndex,
                geositeDirectIndex,
                resolveIndex,
                geoipRejectIndex,
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
            Options.Create(new SingboxOptions
            {
                AdGuardDnsRuleSetUrl = AdGuardDnsRuleSetUrl
            }),
            Options.Create(new TailscaleOptions()));

    private static int FindUdp443RejectIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Reject
            && ContainsUdp443Condition(rule)
            && !ContainsIpv6Condition(rule)
            && ReferencedRuleSets(rule).Contains(ruleSet));

    private static int FindSniffIndex(RouteConfig route, string network) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Sniff
            && rule.Network?.Contains(network) == true);

    private static int FindRouteRuleIndex(RouteConfig route, string ruleSet) =>
        route.Rules.FindIndex(rule =>
            rule.Action == RouteRuleAction.Route
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
