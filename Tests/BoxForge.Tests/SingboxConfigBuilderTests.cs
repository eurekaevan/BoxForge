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
    [Test]
    public void RuleSetsUseAnIpv4OnlyDirectHttpClient()
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
            Assert.That(directClient.Detour, Is.Null);
            Assert.That(
                directClient.DomainResolver?.Server,
                Is.EqualTo(SingboxTags.LocalDns));
            Assert.That(
                directClient.DomainResolver?.Strategy,
                Is.EqualTo(DnsStrategy.Ipv4Only));
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

    [Test]
    public void ProxyServerDomainsUseIpv4OnlyResolverObjects()
    {
        var proxy = new VlessOutbound
        {
            Tag = "美国 01",
            Server = "node.example.com",
            ServerPort = 443,
            Uuid = "00000000-0000-4000-8000-000000000001"
        };
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([proxy], [proxy.Tag], [proxy.Server]),
            TargetPlatform.Linux,
            new string('b', 64)));

        ProxyOutbound generated = config.Outbounds
            .OfType<ProxyOutbound>()
            .Single();
        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(
                generated.DomainResolver.Server,
                Is.EqualTo(SingboxTags.NodeResolverDns));
            Assert.That(
                generated.DomainResolver.Strategy,
                Is.EqualTo(DnsStrategy.Ipv4Only));
            Assert.That(json, Does.Contain("\"domain_resolver\": {"));
            Assert.That(json, Does.Contain("\"strategy\": \"ipv4_only\""));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

    [Test]
    public void GoogleServiceDefaultsToUnitedStatesGroupWhenAvailable()
    {
        ProfilePlan plan = ProfilePlanner.Plan(new NodeCatalog(
            [],
            ["美国 01", "美国 02"],
            []));

        SelectorOutbound google = plan.ServiceOutbounds.Single(outbound =>
            outbound.Tag == ServiceGroupNames.Google);
        string unitedStates = ProfileDefinitions.Regions.Single(region =>
            region.Id == RegionId.UnitedStates).DisplayName;

        Assert.Multiple(() =>
        {
            Assert.That(google.Default, Is.EqualTo(unitedStates));
            Assert.That(google.Outbounds, Does.Contain(unitedStates));
        });
    }

    private static SingboxConfigBuilder CreateBuilder()
    {
        var tailscaleOptions = Options.Create(new TailscaleOptions());

        return new SingboxConfigBuilder(
            new TailscaleEndpointBuilder(tailscaleOptions),
            new DnsProfileBuilder(tailscaleOptions),
            new RouteProfileBuilder(tailscaleOptions));
    }
}
