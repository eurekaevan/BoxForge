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
    private const string MainProxyGroup = "custom-main";
    private const string Direct = "custom-direct";

    [Test]
    public void RuleSetsUseOnlyTheDirectHttpClient()
    {
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], [], []),
            TargetPlatform.Android,
            new string('a', 64)));

        HttpClientConfig directClient = config.HttpClients.Single(client =>
            client.Tag == HttpClientTags.RuleSetDirect);

        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(config.HttpClients, Has.Count.EqualTo(1));
            Assert.That(directClient.Detour, Is.EqualTo(Direct));
            Assert.That(
                config.Route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetDirect));
            Assert.That(
                config.Route.RuleSet.All(ruleSet => ruleSet.HttpClient == null),
                Is.True);
            Assert.That(json, Does.Not.Contain("\"http_client\":"));
            Assert.That(json, Does.Not.Contain("\"http_client\": null"));
            Assert.That(json, Does.Contain(AdBlockingRuleSets.AntiAdTag));
            Assert.That(json, Does.Contain(AdBlockingRuleSets.SagerAdsTag));
            Assert.That(json, Does.Not.Contain("adguard-dns"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

    private static SingboxConfigBuilder CreateBuilder()
    {
        var singboxOptions = Options.Create(new SingboxOptions
        {
            MainProxyGroup = MainProxyGroup,
            Direct = Direct
        });
        var tailscaleOptions = Options.Create(new TailscaleOptions());

        return new SingboxConfigBuilder(
            new ProfilePlanner(singboxOptions),
            new TailscaleEndpointBuilder(tailscaleOptions),
            new DnsProfileBuilder(singboxOptions, tailscaleOptions),
            new RouteProfileBuilder(singboxOptions, tailscaleOptions));
    }
}
