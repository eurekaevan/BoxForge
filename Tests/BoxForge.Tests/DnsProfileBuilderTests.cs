using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class DnsProfileBuilderTests
{
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

    private static DnsProfileBuilder CreateBuilder() =>
        new(
            Options.Create(new SingboxOptions()),
            Options.Create(new TailscaleOptions()));
}
