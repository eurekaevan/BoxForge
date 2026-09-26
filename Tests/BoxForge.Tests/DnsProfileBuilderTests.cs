using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class DnsProfileBuilderTests
{
    [TestCase(TargetPlatform.Android, DnsStrategy.Ipv4Only, true)]
    [TestCase(TargetPlatform.Linux, DnsStrategy.Ipv4Only, true)]
    [TestCase(TargetPlatform.Windows, DnsStrategy.Ipv4Only, true)]
    public void AddressFamilyPolicyControlsAaaaBlocking(
        TargetPlatform platform,
        DnsStrategy? strategy,
        bool blocksAaaa)
    {
        DnsConfig dns = CreateBuilder().Build(platform);
        int aaaaIndex = dns.Rules.FindIndex(rule =>
            rule.QueryType?.Contains("AAAA") == true
            && rule.Action == DnsRuleAction.Predefined);

        Assert.Multiple(() =>
        {
            Assert.That(dns.Strategy, Is.EqualTo(strategy));
            Assert.That(aaaaIndex >= 0, Is.EqualTo(blocksAaaa));
            if (blocksAaaa)
            {
                int adsIndex = FindRuleSetIndex(dns.Rules, RuleSetTags.Ads);
                int firstPoolIndex = dns.Rules.FindIndex(rule =>
                    rule.Tag == DnsResponseTags.PriorityPrimary);
                Assert.That(aaaaIndex, Is.LessThan(adsIndex));
                Assert.That(aaaaIndex, Is.LessThan(firstPoolIndex));
                Assert.That(dns.Rules[aaaaIndex].Rcode, Is.EqualTo(DnsResponseCode.NoError));
            }
        });
    }

    [Test]
    public void TailscaleRoutePrecedesAdBlockingAndAdRuleReturnsNxDomain()
    {
        DnsConfig dns = CreateBuilder(tailscaleEnabled: true).Build(TargetPlatform.Linux);
        int tailscaleIndex = dns.Rules.FindIndex(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleDns) == true);
        int adsIndex = FindRuleSetIndex(dns.Rules, RuleSetTags.Ads);
        DnsRule adsRule = dns.Rules[adsIndex];

        Assert.Multiple(() =>
        {
            Assert.That(tailscaleIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(tailscaleIndex, Is.LessThan(adsIndex));
            Assert.That(adsRule.Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(adsRule.Rcode, Is.EqualTo(DnsResponseCode.NameError));
        });
    }

    [Test]
    public void PriorityServiceSetsComeFromDefinitionsAndPrecedeDomesticPool()
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Linux);
        string[] expectedRuleSets = ProfileDefinitions.Services
            .Where(service => service.PrecedesDomesticRoutes)
            .SelectMany(service => service.RuleSets)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        DnsRule primary = dns.Rules.Single(rule =>
            rule.Tag == DnsResponseTags.PriorityPrimary);
        DnsRule secondary = dns.Rules.Single(rule =>
            rule.Tag == DnsResponseTags.PrioritySecondary);
        int adsIndex = FindRuleSetIndex(dns.Rules, RuleSetTags.Ads);
        int priorityIndex = dns.Rules.IndexOf(primary);
        int domesticIndex = dns.Rules.FindIndex(rule =>
            rule.Tag == DnsResponseTags.DomesticPrimary);

        Assert.Multiple(() =>
        {
            Assert.That(primary.RuleSet, Is.EqualTo(expectedRuleSets));
            Assert.That(secondary.RuleSet, Is.EqualTo(expectedRuleSets));
            Assert.That(priorityIndex, Is.GreaterThan(adsIndex));
            Assert.That(priorityIndex, Is.LessThan(domesticIndex));
        });
    }

    [Test]
    public void EveryResolverPoolStartsBothQueriesThenMatchesPrimaryBeforeSecondary()
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Linux);
        List<string> priorityRuleSets = ProfileDefinitions.Services
            .Where(service => service.PrecedesDomesticRoutes)
            .SelectMany(service => service.RuleSets)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        AssertResolverPool(
            dns.Rules,
            DnsResponseTags.PriorityPrimary,
            DnsResponseTags.PrioritySecondary,
            priorityRuleSets,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.ProxyGoogleDns);
        AssertResolverPool(
            dns.Rules,
            DnsResponseTags.DomesticPrimary,
            DnsResponseTags.DomesticSecondary,
            [RuleSetTags.Cn],
            SingboxTags.DirectAliDns,
            SingboxTags.DirectTencentDns);
        AssertResolverPool(
            dns.Rules,
            DnsResponseTags.GlobalPrimary,
            DnsResponseTags.GlobalSecondary,
            null,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.ProxyGoogleDns);
    }

    [TestCase(DnsResponseCode.NoError, DnsResponseCode.NoError,
        DnsResponseCode.NoError, DnsResponseTags.PriorityPrimary)]
    [TestCase(DnsResponseCode.NameError, DnsResponseCode.NoError,
        DnsResponseCode.NameError, DnsResponseTags.PriorityPrimary)]
    [TestCase(null, DnsResponseCode.NoError,
        DnsResponseCode.NoError, DnsResponseTags.PrioritySecondary)]
    [TestCase(DnsResponseCode.ServerFailure, DnsResponseCode.NameError,
        DnsResponseCode.NameError, DnsResponseTags.PrioritySecondary)]
    [TestCase(DnsResponseCode.Refused, DnsResponseCode.ServerFailure,
        DnsResponseCode.ServerFailure, null)]
    [TestCase(null, null, DnsResponseCode.ServerFailure, null)]
    public void PriorityPoolResponsesStayWithinTheirScope(
        DnsResponseCode? primary,
        DnsResponseCode? secondary,
        DnsResponseCode expected,
        string? expectedTag)
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Linux);
        int start = dns.Rules.FindIndex(rule =>
            rule.Tag == DnsResponseTags.PriorityPrimary);
        int end = dns.Rules.FindIndex(start, rule =>
            rule.Action == DnsRuleAction.Predefined
            && rule.Rcode == DnsResponseCode.ServerFailure);
        var upstream = new Dictionary<string, DnsResponseCode?>
        {
            [DnsResponseTags.PriorityPrimary] = primary,
            [DnsResponseTags.PrioritySecondary] = secondary
        };
        DnsRule selected = dns.Rules
            .Skip(start)
            .Take(end - start + 1)
            .First(rule => rule.Action == DnsRuleAction.Predefined
                || rule.Action == DnsRuleAction.Respond
                && rule.MatchResponse is not null
                && upstream[rule.MatchResponse] == rule.ResponseRcode);

        Assert.Multiple(() =>
        {
            Assert.That(selected.MatchResponse, Is.EqualTo(expectedTag));
            Assert.That(selected.ResponseRcode ?? selected.Rcode,
                Is.EqualTo(expected));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AddressFamilyPolicyAlsoControlsRouteIpv6Guards(TargetPlatform platform)
    {
        var options = Options.Create(new TailscaleOptions());
        DnsConfig dns = new DnsProfileBuilder(options).Build(platform);
        RouteConfig route = new RouteProfileBuilder(options).Build(platform);

        Assert.That(AddressFamilyPolicies.For(platform),
            Is.EqualTo(AddressFamilyPolicy.Ipv4Only));
        Assert.That(dns.Strategy, Is.EqualTo(DnsStrategy.Ipv4Only));
        Assert.That(route.Rules.Count(rule =>
            rule.IpVersion == 6 && rule.Action == RouteRuleAction.Reject),
            Is.EqualTo(2));
    }

    [Test]
    public void PoolsEndWithScopedServfailAndDoNotFallThroughToAnotherPool()
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Linux);
        List<string> priorityRuleSets = ProfileDefinitions.Services
            .Where(service => service.PrecedesDomesticRoutes)
            .SelectMany(service => service.RuleSets)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        (string PrimaryTag, string SecondaryTag, List<string>? RuleSets, string? NextTag)[] pools =
        [
            (DnsResponseTags.PriorityPrimary, DnsResponseTags.PrioritySecondary, priorityRuleSets, DnsResponseTags.DomesticPrimary),
            (DnsResponseTags.DomesticPrimary, DnsResponseTags.DomesticSecondary, [RuleSetTags.Cn], DnsResponseTags.GlobalPrimary),
            (DnsResponseTags.GlobalPrimary, DnsResponseTags.GlobalSecondary, null, null)
        ];

        foreach ((string primaryTag, string secondaryTag, List<string>? ruleSets, string? nextTag) in pools)
        {
            int primaryIndex = dns.Rules.FindIndex(rule => rule.Tag == primaryTag);
            int terminalIndex = FindTerminalServfailIndex(dns.Rules, primaryIndex, ruleSets);
            int lastResponseIndex = dns.Rules.FindLastIndex(primaryIndex, rule =>
                rule.MatchResponse == primaryTag || rule.MatchResponse == secondaryTag);

            Assert.That(terminalIndex, Is.GreaterThan(primaryIndex), primaryTag);
            Assert.That(terminalIndex, Is.GreaterThan(lastResponseIndex), primaryTag);
            if (nextTag is not null)
            {
                int nextPoolIndex = dns.Rules.FindIndex(rule => rule.Tag == nextTag);
                Assert.That(terminalIndex, Is.LessThan(nextPoolIndex), primaryTag);
            }
        }
    }

    [Test]
    public void ResolverServersHaveStableTransportIdentityAndCachePolicy()
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Linux);
        Dictionary<string, HttpsDnsServer> servers = dns.Servers
            .OfType<HttpsDnsServer>()
            .ToDictionary(server => server.Tag!, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(servers.Keys, Does.Contain(SingboxTags.DirectAliDns));
            Assert.That(servers.Keys, Does.Contain(SingboxTags.DirectTencentDns));
            Assert.That(servers.Keys, Does.Contain(SingboxTags.ProxyCloudflareDns));
            Assert.That(servers.Keys, Does.Contain(SingboxTags.ProxyGoogleDns));
            Assert.That(servers[SingboxTags.DirectAliDns].Type, Is.EqualTo("https"));
            Assert.That(servers[SingboxTags.DirectAliDns].Server, Is.EqualTo("223.5.5.5"));
            Assert.That(servers[SingboxTags.DirectAliDns].Tls?.ServerName, Is.EqualTo("dns.alidns.com"));
            Assert.That(servers[SingboxTags.DirectTencentDns].Server, Is.EqualTo("119.29.29.29"));
            Assert.That(servers[SingboxTags.DirectTencentDns].Tls?.ServerName, Is.EqualTo("doh.pub"));
            Assert.That(servers[SingboxTags.ProxyCloudflareDns].Server, Is.EqualTo("1.1.1.1"));
            Assert.That(servers[SingboxTags.ProxyCloudflareDns].Tls?.ServerName, Is.EqualTo("cloudflare-dns.com"));
            Assert.That(servers[SingboxTags.ProxyGoogleDns].Server, Is.EqualTo("8.8.8.8"));
            Assert.That(servers[SingboxTags.ProxyGoogleDns].Tls?.ServerName, Is.EqualTo("dns.google"));
            Assert.That(servers[SingboxTags.DirectAliDns].Detour, Is.Null);
            Assert.That(servers[SingboxTags.DirectTencentDns].Detour, Is.Null);
            Assert.That(servers[SingboxTags.ProxyCloudflareDns].Detour, Is.EqualTo(SingboxTags.MainProxyGroup));
            Assert.That(servers[SingboxTags.ProxyGoogleDns].Detour, Is.EqualTo(SingboxTags.MainProxyGroup));
            Assert.That(dns.CacheCapacity, Is.EqualTo(4096));
            Assert.That(dns.Final, Is.EqualTo(SingboxTags.ProxyCloudflareDns));
            Assert.That(dns.Optimistic.Enabled, Is.True);
            Assert.That(dns.Optimistic.Timeout, Is.EqualTo("6h"));
            Assert.That(dns.ReverseMapping, Is.True);
        });
    }

    [Test]
    public void BuilderDoesNotEmitNodeDomainSpecificDnsRules()
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Linux);

        Assert.That(dns.Rules.Any(rule => rule.Domain is not null), Is.False);
    }

    [Test]
    public void TailscaleResolverDisablesOptimisticCache()
    {
        DnsConfig dns = CreateBuilder(tailscaleEnabled: true).Build(TargetPlatform.Linux);
        DnsRule tailscaleRule = dns.Rules.Single(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleDns) == true);

        Assert.That(tailscaleRule.DisableOptimisticCache, Is.True);
    }

    private static void AssertResolverPool(
        List<DnsRule> rules,
        string primaryTag,
        string secondaryTag,
        List<string>? expectedRuleSets,
        string expectedPrimaryServer,
        string expectedSecondaryServer)
    {
        int primaryIndex = rules.FindIndex(rule => rule.Tag == primaryTag);
        int secondaryIndex = rules.FindIndex(rule => rule.Tag == secondaryTag);
        Assert.That(primaryIndex, Is.GreaterThanOrEqualTo(0), primaryTag);
        Assert.That(secondaryIndex, Is.EqualTo(primaryIndex + 1), secondaryTag);

        DnsRule primary = rules[primaryIndex];
        DnsRule secondary = rules[secondaryIndex];
        List<DnsRule> responses = rules
            .Skip(secondaryIndex + 1)
            .TakeWhile(rule => rule.MatchResponse is not null)
            .ToList();
        string[] responseOrder =
        [
            primaryTag,
            primaryTag,
            secondaryTag,
            secondaryTag
        ];
        DnsResponseCode[] responseCodes =
        [
            DnsResponseCode.NoError,
            DnsResponseCode.NameError,
            DnsResponseCode.NoError,
            DnsResponseCode.NameError
        ];

        Assert.Multiple(() =>
        {
            Assert.That(primary.Action, Is.EqualTo(DnsRuleAction.Evaluate));
            Assert.That(secondary.Action, Is.EqualTo(DnsRuleAction.Evaluate));
            Assert.That(primary.Server, Is.EqualTo(expectedPrimaryServer));
            Assert.That(secondary.Server, Is.EqualTo(expectedSecondaryServer));
            Assert.That(primary.RuleSet, Is.EqualTo(expectedRuleSets));
            Assert.That(secondary.RuleSet, Is.EqualTo(expectedRuleSets));
            Assert.That(responses.Select(rule => rule.MatchResponse), Is.EqualTo(responseOrder));
            Assert.That(responses.Select(rule => rule.ResponseRcode), Is.EqualTo(responseCodes));
            Assert.That(responses.All(rule => rule.Action == DnsRuleAction.Respond), Is.True);
            Assert.That(responses.All(rule => rule.RuleSet?.SequenceEqual(expectedRuleSets ?? [])
                ?? expectedRuleSets is null), Is.True);
        });
    }

    private static int FindTerminalServfailIndex(
        List<DnsRule> rules,
        int poolStartIndex,
        List<string>? ruleSets) => rules.FindIndex(poolStartIndex, rule =>
        rule.Action == DnsRuleAction.Predefined
        && rule.Rcode == DnsResponseCode.ServerFailure
        && ((rule.RuleSet is null && ruleSets is null)
            || (rule.RuleSet is not null && ruleSets is not null
                && rule.RuleSet.SequenceEqual(ruleSets))));

    private static int FindRuleSetIndex(List<DnsRule> rules, string ruleSet) =>
        rules.FindIndex(rule => rule.RuleSet?.Contains(ruleSet) == true);

    private static DnsProfileBuilder CreateBuilder(bool tailscaleEnabled = false) =>
        new(Options.Create(new TailscaleOptions
        {
            Enabled = tailscaleEnabled,
            AndroidEnabled = tailscaleEnabled
        }));
}
