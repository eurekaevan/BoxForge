using System.Text.Json;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SingboxConfigBuilderTests
{
    private const string AdGuardDnsRuleSetUrl =
        "https://sublinks.skuld.workers.dev/rules/adguard-dns.srs";
    private const string MainProxyGroup = "custom-main";
    private const string Direct = "custom-direct";

    [Test]
    public void RuleSetsUseDirectAndProxyHttpClientsAsConfigured()
    {
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], [], []),
            TargetPlatform.Android,
            new string('a', 64)));

        HttpClientConfig directClient = config.HttpClients.Single(client =>
            client.Tag == HttpClientTags.RuleSetDirect);
        HttpClientConfig proxyClient = config.HttpClients.Single(client =>
            client.Tag == HttpClientTags.RuleSetProxy);
        SingboxRuleSet adGuardRuleSet = config.Route.RuleSet.Single(ruleSet =>
            ruleSet.Tag == SingboxOptions.AdGuardDnsRuleSetTag);
        SingboxRuleSet ordinaryRuleSet = config.Route.RuleSet.First(ruleSet =>
            ruleSet.Tag != SingboxOptions.AdGuardDnsRuleSetTag);

        string json = new ConfigSerializer().Serialize(config);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement serializedOrdinaryRuleSet = document.RootElement
            .GetProperty("route")
            .GetProperty("rule_set")
            .EnumerateArray()
            .Single(ruleSet => ruleSet.GetProperty("tag").GetString()
                == ordinaryRuleSet.Tag);

        Assert.Multiple(() =>
        {
            Assert.That(config.HttpClients, Has.Count.EqualTo(2));
            Assert.That(
                config.HttpClients.Select(client => client.Tag),
                Is.Unique);
            Assert.That(directClient.Detour, Is.EqualTo(Direct));
            Assert.That(proxyClient.Detour, Is.EqualTo(MainProxyGroup));
            Assert.That(
                config.Route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetDirect));
            Assert.That(
                adGuardRuleSet.HttpClient,
                Is.EqualTo(HttpClientTags.RuleSetProxy));
            Assert.That(
                config.Route.RuleSet.Where(ruleSet =>
                    ruleSet.Tag != SingboxOptions.AdGuardDnsRuleSetTag)
                    .All(ruleSet => ruleSet.HttpClient == null),
                Is.True);
            Assert.That(
                serializedOrdinaryRuleSet.TryGetProperty("http_client", out _),
                Is.False);
            Assert.That(json, Does.Not.Contain("\"http_client\": null"));
            Assert.That(json, Does.Not.Contain("geosite-category-ads-all"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

    private static SingboxConfigBuilder CreateBuilder()
    {
        var singboxOptions = Options.Create(new SingboxOptions
        {
            MainProxyGroup = MainProxyGroup,
            Direct = Direct,
            AdGuardDnsRuleSetUrl = AdGuardDnsRuleSetUrl
        });
        var tailscaleOptions = Options.Create(new TailscaleOptions());

        return new SingboxConfigBuilder(
            new ProfilePlanner(singboxOptions),
            new TailscaleEndpointBuilder(tailscaleOptions),
            new DnsProfileBuilder(singboxOptions, tailscaleOptions),
            new RouteProfileBuilder(singboxOptions, tailscaleOptions));
    }
}
