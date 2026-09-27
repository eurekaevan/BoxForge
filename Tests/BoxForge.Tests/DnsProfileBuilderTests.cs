using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class DnsProfileBuilderTests
{
    [TestCase(TargetPlatform.Android, DnsResolverSelectionMode.SequentialFallback)]
    [TestCase(TargetPlatform.Linux, DnsResolverSelectionMode.ParallelFastest)]
    [TestCase(TargetPlatform.Windows, DnsResolverSelectionMode.ParallelFastest)]
    public void ResolverSelectionPolicyMapsPlatforms(
        TargetPlatform platform,
        DnsResolverSelectionMode expected)
    {
        Assert.That(DnsResolverSelectionPolicies.For(platform), Is.EqualTo(expected));
    }

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
    public void AndroidPoolsQuerySecondaryOnlyAfterPrimaryIsUnacceptable()
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Android);
        List<string> priorityRuleSets = ProfileDefinitions.Services
            .Where(service => service.PrecedesDomesticRoutes)
            .SelectMany(service => service.RuleSets)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        AssertSequentialFallbackPool(
            dns.Rules,
            DnsResponseTags.PriorityPrimary,
            DnsResponseTags.PrioritySecondary,
            priorityRuleSets,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.ProxyGoogleDns);
        AssertSequentialFallbackPool(
            dns.Rules,
            DnsResponseTags.DomesticPrimary,
            DnsResponseTags.DomesticSecondary,
            [RuleSetTags.Cn],
            SingboxTags.DirectAliDns,
            SingboxTags.DirectTencentDns);
        AssertSequentialFallbackPool(
            dns.Rules,
            DnsResponseTags.GlobalPrimary,
            DnsResponseTags.GlobalSecondary,
            null,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.ProxyGoogleDns);
    }

    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void DesktopPoolsRaceOnlyNoErrorAndPreferPrimaryNxDomain(
        TargetPlatform platform)
    {
        DnsConfig dns = CreateBuilder().Build(platform);
        List<string> priorityRuleSets = ProfileDefinitions.Services
            .Where(service => service.PrecedesDomesticRoutes)
            .SelectMany(service => service.RuleSets)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        AssertParallelFastestPool(
            dns.Rules,
            DnsResponseTags.PriorityPrimary,
            DnsResponseTags.PrioritySecondary,
            priorityRuleSets,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.ProxyGoogleDns);
        AssertParallelFastestPool(
            dns.Rules,
            DnsResponseTags.DomesticPrimary,
            DnsResponseTags.DomesticSecondary,
            [RuleSetTags.Cn],
            SingboxTags.DirectAliDns,
            SingboxTags.DirectTencentDns);
        AssertParallelFastestPool(
            dns.Rules,
            DnsResponseTags.GlobalPrimary,
            DnsResponseTags.GlobalSecondary,
            null,
            SingboxTags.ProxyCloudflareDns,
            SingboxTags.ProxyGoogleDns);
    }

    [Test]
    public void DesktopPriorityRuleShapeEncodesPositiveRaceAndOrderedNxDomain()
    {
        DnsConfig dns = CreateBuilder().Build(TargetPlatform.Linux);
        List<DnsRule> pool = GetPoolRules(
            dns.Rules,
            DnsResponseTags.PriorityPrimary);

        List<DnsRule> bothPositive = MatchingResponses(
            pool, DnsResponseCode.NoError, DnsResponseCode.NoError);
        List<DnsRule> primaryNxSecondaryPositive = MatchingResponses(
            pool, DnsResponseCode.NameError, DnsResponseCode.NoError);
        List<DnsRule> bothNx = MatchingResponses(
            pool, DnsResponseCode.NameError, DnsResponseCode.NameError);
        List<DnsRule> primaryFailureSecondaryPositive = MatchingResponses(
            pool, null, DnsResponseCode.NoError);
        List<DnsRule> bothFailure = MatchingResponses(pool, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(bothPositive.Select(rule => rule.MatchResponse),
                Is.EqualTo(new[]
                {
                    DnsResponseTags.PriorityPrimary,
                    DnsResponseTags.PrioritySecondary
                }));
            Assert.That(bothPositive.All(rule => rule.Race == true), Is.True);
            Assert.That(primaryNxSecondaryPositive.Select(rule =>
                    (rule.MatchResponse, rule.ResponseRcode, rule.Race)),
                Is.EqualTo(new[]
                {
                    (DnsResponseTags.PrioritySecondary,
                        (DnsResponseCode?)DnsResponseCode.NoError, (bool?)true),
                    (DnsResponseTags.PriorityPrimary,
                        (DnsResponseCode?)DnsResponseCode.NameError, (bool?)null)
                }));
            Assert.That(bothNx.Select(rule => rule.MatchResponse),
                Is.EqualTo(new[]
                {
                    DnsResponseTags.PriorityPrimary,
                    DnsResponseTags.PrioritySecondary
                }));
            Assert.That(bothNx.All(rule => rule.Race is null), Is.True);
            Assert.That(primaryFailureSecondaryPositive.Single().MatchResponse,
                Is.EqualTo(DnsResponseTags.PrioritySecondary));
            Assert.That(primaryFailureSecondaryPositive.Single().Race, Is.True);
            Assert.That(bothFailure, Is.Empty);
            Assert.That(pool[^1].Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(pool[^1].Rcode, Is.EqualTo(DnsResponseCode.ServerFailure));
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

    private static void AssertSequentialFallbackPool(
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
        Assert.That(secondaryIndex, Is.EqualTo(primaryIndex + 3), secondaryTag);

        DnsRule primary = rules[primaryIndex];
        DnsRule secondary = rules[secondaryIndex];
        List<DnsRule> primaryResponses = rules
            .Skip(primaryIndex + 1)
            .Take(2)
            .ToList();
        List<DnsRule> secondaryResponses = rules
            .Skip(secondaryIndex + 1)
            .Take(2)
            .ToList();
        int terminalIndex = secondaryIndex + 3;

        Assert.Multiple(() =>
        {
            AssertEvaluate(primary, expectedRuleSets, expectedPrimaryServer,
                primaryTag, "2s");
            AssertEvaluate(secondary, expectedRuleSets, expectedSecondaryServer,
                secondaryTag, null);
            AssertResponses(primaryResponses, expectedRuleSets,
                [primaryTag, primaryTag],
                [DnsResponseCode.NoError, DnsResponseCode.NameError],
                [null, null]);
            AssertResponses(secondaryResponses, expectedRuleSets,
                [secondaryTag, secondaryTag],
                [DnsResponseCode.NoError, DnsResponseCode.NameError],
                [null, null]);
            AssertTerminalServfail(rules[terminalIndex], expectedRuleSets);
        });
    }

    private static List<DnsRule> GetPoolRules(
        List<DnsRule> rules,
        string primaryTag)
    {
        int start = rules.FindIndex(rule => rule.Tag == primaryTag);
        int end = rules.FindIndex(start, rule =>
            rule.Action == DnsRuleAction.Predefined
            && rule.Rcode == DnsResponseCode.ServerFailure);
        return rules.Skip(start).Take(end - start + 1).ToList();
    }

    private static List<DnsRule> MatchingResponses(
        List<DnsRule> pool,
        DnsResponseCode? primary,
        DnsResponseCode? secondary) =>
        pool.Where(rule => rule.Action == DnsRuleAction.Respond
                && rule.ResponseRcode == (rule.MatchResponse ==
                    DnsResponseTags.PriorityPrimary ? primary : secondary))
            .ToList();

    private static void AssertParallelFastestPool(
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
        List<DnsRule> responses = rules.Skip(secondaryIndex + 1).Take(4).ToList();
        int terminalIndex = secondaryIndex + 5;
        string[] responseOrder =
        [
            primaryTag,
            secondaryTag,
            primaryTag,
            secondaryTag
        ];
        DnsResponseCode[] responseCodes =
        [
            DnsResponseCode.NoError,
            DnsResponseCode.NoError,
            DnsResponseCode.NameError,
            DnsResponseCode.NameError
        ];

        Assert.Multiple(() =>
        {
            AssertEvaluate(primary, expectedRuleSets, expectedPrimaryServer,
                primaryTag, null);
            AssertEvaluate(secondary, expectedRuleSets, expectedSecondaryServer,
                secondaryTag, null);
            AssertResponses(responses, expectedRuleSets, responseOrder, responseCodes,
                [true, true, null, null]);
            AssertTerminalServfail(rules[terminalIndex], expectedRuleSets);
        });
    }

    private static void AssertEvaluate(
        DnsRule rule,
        List<string>? expectedRuleSets,
        string expectedServer,
        string expectedTag,
        string? expectedTimeout)
    {
        Assert.Multiple(() =>
        {
            Assert.That(rule.Action, Is.EqualTo(DnsRuleAction.Evaluate));
            Assert.That(rule.Server, Is.EqualTo(expectedServer));
            Assert.That(rule.Tag, Is.EqualTo(expectedTag));
            Assert.That(rule.RuleSet, Is.EqualTo(expectedRuleSets));
            Assert.That(rule.Timeout, Is.EqualTo(expectedTimeout));
        });
    }

    private static void AssertResponses(
        List<DnsRule> responses,
        List<string>? expectedRuleSets,
        string[] expectedTags,
        DnsResponseCode[] expectedCodes,
        bool?[] expectedRace)
    {
        Assert.Multiple(() =>
        {
            Assert.That(responses.Select(rule => rule.MatchResponse),
                Is.EqualTo(expectedTags));
            Assert.That(responses.Select(rule => rule.ResponseRcode),
                Is.EqualTo(expectedCodes));
            Assert.That(responses.Select(rule => rule.Race), Is.EqualTo(expectedRace));
            Assert.That(responses.All(rule => rule.Action == DnsRuleAction.Respond),
                Is.True);
            Assert.That(responses.All(rule =>
                    rule.RuleSet?.SequenceEqual(expectedRuleSets ?? [])
                    ?? expectedRuleSets is null),
                Is.True);
        });
    }

    private static void AssertTerminalServfail(
        DnsRule rule,
        List<string>? expectedRuleSets)
    {
        Assert.Multiple(() =>
        {
            Assert.That(rule.Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(rule.Rcode, Is.EqualTo(DnsResponseCode.ServerFailure));
            Assert.That(rule.RuleSet, Is.EqualTo(expectedRuleSets));
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
