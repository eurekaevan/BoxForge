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
            rule.PreferredBy?.Contains("tailscale-dns") == true);
        int nodeResolverIndex = dns.Rules.FindIndex(rule =>
            rule.Domain?.Contains("node.example.com") == true
            && rule.Server == SingboxTags.NodeResolverDns);
        int adBlockingIndex = dns.Rules.FindIndex(rule =>
            rule.RuleSet?.SequenceEqual(
                [
                    AdBlockingRuleSets.AntiAdTag,
                    AdBlockingRuleSets.SagerAdsTag
                ]) == true);
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
            Assert.That(json, Does.Contain(AdBlockingRuleSets.AntiAdTag));
            Assert.That(json, Does.Contain(AdBlockingRuleSets.SagerAdsTag));
            Assert.That(json, Does.Not.Contain("adguard-dns"));
        });
    }

    [Test]
    public void GoogleRemoteDnsRacePrecedesDomesticDnsRace()
    {
        DnsConfig dns = CreateBuilder().Build(
            new NodeCatalog([], [], []),
            TargetPlatform.Linux);

        int adBlockingIndex = dns.Rules.FindIndex(rule =>
            rule.RuleSet?.Contains(AdBlockingRuleSets.AntiAdTag) == true);
        int serviceAaaaBlockIndex = dns.Rules.FindIndex(rule =>
            rule.QueryType?.Contains("AAAA") == true
            && rule.RuleSet?.Contains("geosite-google") == true
            && rule.Action == DnsRuleAction.Predefined);
        int googleFirstIndex = dns.Rules.FindIndex(rule =>
            rule.Action == DnsRuleAction.Evaluate
            && rule.Tag == "google-first");
        int googleLastIndex = dns.Rules.FindLastIndex(rule =>
            rule.RuleSet?.Contains("geosite-google") == true);
        int domesticFirstIndex = dns.Rules.FindIndex(rule =>
            rule.Action == DnsRuleAction.Evaluate
            && rule.Tag == "cn-first");

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
                Is.EqualTo(SingboxTags.RemoteGoogleDns));
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
            && rule.Tag == "cn-first");
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
            && rule.Tag == "global-first");

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
                Enabled = tailscaleEnabled
            }));
}
