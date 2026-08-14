using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BoxForge.Tests;

[TestFixture]
public sealed class DnsProfileBuilderTests
{
    [Test]
    public void TailscaleAndNodeResolutionPrecedeAdBlockingNxDomainRule()
    {
        var nodes = new NodeCatalog([], [], ["node.example.com"]);
        DnsConfig dns = CreateBuilder(tailscaleEnabled: true).Build(nodes);

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
    public void DomesticRulesAnswerAaaaBeforeOtherAaaaIsBlocked()
    {
        var nodes = new NodeCatalog([], [], ["node.example.cn"]);
        DnsConfig dns = CreateBuilder().Build(nodes);

        int nodeAResolverIndex = dns.Rules.FindIndex(rule =>
            rule.Domain?.Contains("node.example.cn") == true);
        int domesticFirstIndex = dns.Rules.FindIndex(rule =>
            rule.Action == DnsRuleAction.Evaluate
            && rule.Tag == "cn-first");
        int domesticLastIndex = dns.Rules.FindLastIndex(rule =>
            rule.RuleSet?.Contains("geosite-cn") == true);
        int otherAaaaBlockIndex = dns.Rules.FindIndex(rule =>
            rule.QueryType?.Contains("AAAA") == true
            && rule.Action == DnsRuleAction.Predefined);
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
                Is.EqualTo(1));
        });
    }

    private static DnsProfileBuilder CreateBuilder(bool tailscaleEnabled = false) =>
        new(
            Options.Create(new SingboxOptions()),
            Options.Create(new TailscaleOptions
            {
                Enabled = tailscaleEnabled
            }));
}
