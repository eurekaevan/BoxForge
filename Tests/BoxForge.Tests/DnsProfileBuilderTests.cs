using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BoxForge.Tests;

[TestFixture]
public sealed class DnsProfileBuilderTests
{
    [Test]
    public void BootstrapIsOmittedWithoutATailscaleEndpoint()
    {
        DnsConfig dns = CreateBuilder().Build(
            new NodeCatalog([], [], []),
            TargetPlatform.Linux);

        Assert.That(
            dns.Servers.Any(server => server.Tag == SingboxTags.BootstrapDns),
            Is.False);
    }

    [Test]
    public void DnsServersAndPrimaryResponsesUseSemanticTags()
    {
        DnsConfig dns = CreateBuilder().Build(
            new NodeCatalog([], [], []),
            TargetPlatform.Linux);

        Assert.Multiple(() =>
        {
            Assert.That(
                dns.Servers.Select(server => server.Tag),
                Is.EqualTo(new[]
                {
                    "dns-node",
                    "dns-cn-tencent",
                    "dns-cn-alidns",
                    "dns-proxy-google",
                    "dns-proxy-cloudflare"
                }));
            Assert.That(
                dns.Rules.Where(rule => rule.Action == DnsRuleAction.Evaluate)
                    .Select(rule => rule.Tag),
                Is.EqualTo(new[]
                {
                    "response-google-cloudflare",
                    "response-cn-tencent",
                    "response-global-cloudflare"
                }));
        });
    }

    [Test]
    public void BootstrapUsesDirectHttpsWithoutTheSystemResolver()
    {
        DnsConfig dns = CreateBuilder(tailscaleEnabled: true).Build(
            new NodeCatalog([], [], []),
            TargetPlatform.Linux);

        HttpsDnsServer bootstrap = dns.Servers
            .OfType<HttpsDnsServer>()
            .Single(server => server.Tag == SingboxTags.BootstrapDns);
        string json = new ConfigSerializer().Serialize(new SingboxConfig
        {
            Dns = dns
        });
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement serializedBootstrap = document.RootElement
            .GetProperty("dns")
            .GetProperty("servers")
            .EnumerateArray()
            .Single(server => server.GetProperty("tag").GetString()
                == SingboxTags.BootstrapDns);

        Assert.Multiple(() =>
        {
            Assert.That(bootstrap.Type, Is.EqualTo("https"));
            Assert.That(bootstrap.Server, Is.EqualTo("223.5.5.5"));
            Assert.That(bootstrap.Tls?.Enabled, Is.True);
            Assert.That(
                bootstrap.Tls?.ServerName,
                Is.EqualTo("dns.alidns.com"));
            Assert.That(bootstrap.Detour, Is.Null);
            Assert.That(
                dns.Servers.OfType<LocalDnsServer>()
                    .Any(server => server.Tag == SingboxTags.BootstrapDns),
                Is.False);
            Assert.That(
                serializedBootstrap.GetProperty("type").GetString(),
                Is.EqualTo("https"));
            Assert.That(
                serializedBootstrap.TryGetProperty("detour", out _),
                Is.False);
        });
    }

    [Test]
    public void TailscaleAndNodeResolutionPrecedeAdBlockingNxDomainRule()
    {
        var nodes = new NodeCatalog([], [], ["node.example.com"]);
        DnsConfig dns = CreateBuilder(tailscaleEnabled: true).Build(
            nodes,
            TargetPlatform.Linux);

        int tailscaleIndex = dns.Rules.FindIndex(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleDns) == true);
        int nodeResolverIndex = dns.Rules.FindIndex(rule =>
            rule.Domain?.Contains("node.example.com") == true
            && rule.Server == SingboxTags.NodeResolverDns);
        int adBlockingIndex = dns.Rules.FindIndex(rule =>
            rule.RuleSet?.SequenceEqual(
                [AdBlockingRuleSets.SagerAdsTag]) == true);
        DnsRule adBlockingRule = dns.Rules[adBlockingIndex];

        string json = new ConfigSerializer().Serialize(new SingboxConfig
        {
            Dns = dns
        });
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement serializedAdBlockingRule = document.RootElement
            .GetProperty("dns")
            .GetProperty("rules")[adBlockingIndex];

        Assert.Multiple(() =>
        {
            Assert.That(
                new[] { tailscaleIndex, nodeResolverIndex, adBlockingIndex },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(adBlockingRule.Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(adBlockingRule.Rcode, Is.EqualTo(DnsResponseCode.NameError));
            Assert.That(
                serializedAdBlockingRule.GetProperty("rcode").GetString(),
                Is.EqualTo("NXDOMAIN"));
            Assert.That(json, Does.Contain(AdBlockingRuleSets.SagerAdsTag));
            Assert.That(json, Does.Not.Contain("anti-ad"));
            Assert.That(json, Does.Not.Contain("adguard-dns"));
        });
    }

    [Test]
    public void GoogleRemoteDnsPrecedesDomesticDns()
    {
        DnsConfig dns = CreateBuilder().Build(
            new NodeCatalog([], [], []),
            TargetPlatform.Linux);

        int adBlockingIndex = dns.Rules.FindIndex(rule =>
            rule.RuleSet?.Contains(AdBlockingRuleSets.SagerAdsTag) == true);
        int serviceAaaaBlockIndex = dns.Rules.FindIndex(rule =>
            rule.QueryType?.Contains("AAAA") == true
            && rule.RuleSet?.Contains("geosite-google") == true
            && rule.Action == DnsRuleAction.Predefined);
        int googleFirstIndex = dns.Rules.FindIndex(rule =>
            rule.Action == DnsRuleAction.Evaluate
            && rule.Tag == DnsResponseTags.GooglePrimary);
        int googleLastIndex = dns.Rules.FindLastIndex(rule =>
            rule.RuleSet?.Contains("geosite-google") == true);
        int domesticFirstIndex = dns.Rules.FindIndex(rule =>
            rule.Action == DnsRuleAction.Evaluate
            && rule.Tag == DnsResponseTags.ChinaPrimary);

        Assert.Multiple(() =>
        {
            Assert.That(
                new[]
                {
                    adBlockingIndex,
                    serviceAaaaBlockIndex,
                    googleFirstIndex,
                    googleLastIndex,
                    domesticFirstIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(
                dns.Rules[googleFirstIndex].RuleSet,
                Is.EqualTo(new[] { "geosite-google" }));
            Assert.That(
                dns.Rules[googleFirstIndex].Server,
                Is.EqualTo(SingboxTags.RemoteDns));
            Assert.That(
                dns.Rules[serviceAaaaBlockIndex].RuleSet,
                Is.EquivalentTo(ProfileDefinitions.Services
                    .SelectMany(service => service.RuleSets)
                    .Distinct(StringComparer.Ordinal)));
        });
    }

    [Test]
    public void DomesticRulesAnswerAaaaBeforeOtherAaaaIsBlocked()
    {
        var nodes = new NodeCatalog([], [], ["node.example.cn"]);
        DnsConfig dns = CreateBuilder().Build(nodes, TargetPlatform.Linux);

        int nodeAResolverIndex = dns.Rules.FindIndex(rule =>
            rule.Domain?.Contains("node.example.cn") == true);
        int domesticFirstIndex = dns.Rules.FindIndex(rule =>
            rule.Action == DnsRuleAction.Evaluate
            && rule.Tag == DnsResponseTags.ChinaPrimary);
        int domesticLastIndex = dns.Rules.FindLastIndex(rule =>
            rule.RuleSet?.Contains("geosite-cn") == true);
        int otherAaaaBlockIndex = dns.Rules.FindIndex(rule =>
            rule.QueryType?.Contains("AAAA") == true
            && rule.Action == DnsRuleAction.Predefined
            && rule.RuleSet == null);
        int serviceAaaaBlockIndex = dns.Rules.FindIndex(rule =>
            rule.QueryType?.Contains("AAAA") == true
            && rule.Action == DnsRuleAction.Predefined
            && rule.RuleSet != null);
        int globalFirstIndex = dns.Rules.FindIndex(rule =>
            rule.Action == DnsRuleAction.Evaluate
            && rule.Tag == DnsResponseTags.GlobalPrimary);

        Assert.Multiple(() =>
        {
            Assert.That(dns.Strategy, Is.EqualTo(DnsStrategy.PreferIpv4));
            Assert.That(
                new[]
                {
                    nodeAResolverIndex,
                    serviceAaaaBlockIndex,
                    domesticFirstIndex,
                    domesticLastIndex,
                    otherAaaaBlockIndex,
                    globalFirstIndex
                },
                Is.Ordered.And.All.GreaterThanOrEqualTo(0));
            Assert.That(dns.Rules[nodeAResolverIndex].QueryType, Is.EqualTo(new[] { "A" }));
            Assert.That(
                dns.Rules.Count(rule =>
                    rule.QueryType?.Contains("AAAA") == true
                    && rule.Action == DnsRuleAction.Predefined),
                Is.EqualTo(2));
        });
    }

    private static DnsProfileBuilder CreateBuilder(bool tailscaleEnabled = false) =>
        new(
            Options.Create(new TailscaleOptions
            {
                Enabled = tailscaleEnabled,
                AndroidEnabled = tailscaleEnabled
            }));

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void PrimaryFallbackChainsUseBoundedSequentialQueries(TargetPlatform platform)
    {
        DnsConfig dns = CreateBuilder().Build(new NodeCatalog([], [], []), platform);
        var expected = new[]
        {
            (DnsResponseTags.GooglePrimary, SingboxTags.RemoteDns, SingboxTags.RemoteGoogleDns, "2s"),
            (DnsResponseTags.ChinaPrimary, SingboxTags.LocalTencentDns, SingboxTags.LocalDns, "1s"),
            (DnsResponseTags.GlobalPrimary, SingboxTags.RemoteDns, SingboxTags.RemoteGoogleDns, "2s")
        };

        Assert.That(dns.Timeout, Is.EqualTo("5s"));
        Assert.That(dns.Rules.Count(rule => rule.Action == DnsRuleAction.Evaluate), Is.EqualTo(3));
        foreach (var (tag, primary, fallback, timeout) in expected)
        {
            int index = dns.Rules.FindIndex(rule => rule.Tag == tag);
            DnsRule evaluate = dns.Rules[index];
            DnsRule success = dns.Rules[index + 1];
            DnsRule negative = dns.Rules[index + 2];
            DnsRule backup = dns.Rules[index + 3];
            Assert.Multiple(() =>
            {
                Assert.That(evaluate.Action, Is.EqualTo(DnsRuleAction.Evaluate));
                Assert.That(evaluate.Server, Is.EqualTo(primary));
                Assert.That(evaluate.Timeout, Is.EqualTo(timeout));
                Assert.That(success.MatchResponse, Is.EqualTo(tag));
                Assert.That(success.ResponseRcode, Is.EqualTo(DnsResponseCode.NoError));
                Assert.That(success.IpAcceptAny, Is.Null, "Accept NODATA and non-address records without fallback.");
                Assert.That(success.Action, Is.EqualTo(DnsRuleAction.Respond));
                Assert.That(negative.MatchResponse, Is.EqualTo(tag));
                Assert.That(negative.ResponseRcode, Is.EqualTo(DnsResponseCode.NameError));
                Assert.That(negative.Action, Is.EqualTo(DnsRuleAction.Respond));
                Assert.That(backup.Action, Is.EqualTo(DnsRuleAction.Route));
                Assert.That(backup.Server, Is.EqualTo(fallback));
                Assert.That(backup.Timeout, Is.Null, "Fallback inherits the global query timeout.");
                Assert.That(backup.MatchResponse, Is.Null, "Transport failures must also reach fallback.");
                Assert.That(success.RuleSet, Is.EqualTo(evaluate.RuleSet));
                Assert.That(negative.RuleSet, Is.EqualTo(evaluate.RuleSet));
                Assert.That(backup.RuleSet, Is.EqualTo(evaluate.RuleSet));
            });
        }

        string json = new ConfigSerializer().Serialize(new SingboxConfig { Dns = dns });
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement serializedDns = document.RootElement.GetProperty("dns");
        Assert.That(serializedDns.GetProperty("timeout").GetString(), Is.EqualTo("5s"));
        foreach (JsonElement rule in serializedDns.GetProperty("rules").EnumerateArray())
        {
            Assert.That(rule.TryGetProperty("race", out _), Is.False);
            Assert.That(rule.TryGetProperty("speculative", out _), Is.False);
            if (rule.GetProperty("action").GetString() == "evaluate")
            {
                string? server = rule.GetProperty("server").GetString();
                Assert.That(rule.GetProperty("timeout").GetString(),
                    Is.EqualTo(server == SingboxTags.LocalTencentDns ? "1s" : "2s"));
            }
        }
    }
}
