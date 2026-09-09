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
    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllPlatformsKeepMixedInboundWithoutSystemProxy(
        TargetPlatform platform)
    {
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], [], []),
            platform,
            new string('a', 64)));

        Inbound tunInbound = config.Inbounds.Single(inbound =>
            inbound.Tag == SingboxTags.TunInbound);
        Inbound mixedInbound = config.Inbounds.Single(inbound =>
            inbound.Tag == SingboxTags.MixedInbound);
        bool hasBridge = config.Outbounds
            .OfType<BridgeOutbound>()
            .Any(outbound => outbound.Tag == SingboxTags.BridgeOutbound);
        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(tunInbound.Type, Is.EqualTo("tun"));
            Assert.That(mixedInbound.Type, Is.EqualTo("mixed"));
            Assert.That(mixedInbound.Listen, Is.EqualTo("127.0.0.1"));
            Assert.That(mixedInbound.ListenPort, Is.EqualTo(8848));
            Assert.That(
                hasBridge,
                Is.EqualTo(platform != TargetPlatform.Android));
            Assert.That(json, Does.Not.Contain("\"set_system_proxy\""));
            Assert.That(json, Does.Not.Contain("\"platform\""));
            Assert.That(json, Does.Not.Contain("\"http_proxy\""));
            Assert.That(
                json.Contains("\"type\": \"bridge\"", StringComparison.Ordinal),
                Is.EqualTo(platform != TargetPlatform.Android));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

    [TestCase(TargetPlatform.Android, false)]
    [TestCase(TargetPlatform.Linux, true)]
    [TestCase(TargetPlatform.Windows, true)]
    public void GeneralTailscaleSettingLeavesAndroidDisabledByDefault(
        TargetPlatform platform,
        bool expectedEnabled)
    {
        SingboxConfig config = CreateBuilder(new TailscaleOptions
        {
            Enabled = true
        }).Build(new SingboxBuildRequest(
            new NodeCatalog([], [], []),
            platform,
            new string('b', 64)));

        bool hasEndpoint = config.Endpoints?
            .OfType<TailscaleEndpoint>()
            .Any() == true;
        bool hasDnsServer = config.Dns.Servers
            .OfType<TailscaleDnsServer>()
            .Any();
        bool hasBootstrap = config.Dns.Servers.Any(server =>
            server.Tag == SingboxTags.BootstrapDns);
        bool hasRoute = config.Route.Rules.Any(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleEndpoint) == true);
        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(hasEndpoint, Is.EqualTo(expectedEnabled));
            Assert.That(hasDnsServer, Is.EqualTo(expectedEnabled));
            Assert.That(hasBootstrap, Is.EqualTo(expectedEnabled));
            Assert.That(hasRoute, Is.EqualTo(expectedEnabled));
            Assert.That(
                json.Contains("\"tailscale\"", StringComparison.Ordinal),
                Is.EqualTo(expectedEnabled));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

    [Test]
    public void AndroidTailscaleCanBeEnabledExplicitly()
    {
        SingboxConfig config = CreateBuilder(new TailscaleOptions
        {
            AndroidEnabled = true
        }).Build(new SingboxBuildRequest(
            new NodeCatalog([], [], []),
            TargetPlatform.Android,
            new string('c', 64)));

        Assert.Multiple(() =>
        {
            Assert.That(
                config.Endpoints?.OfType<TailscaleEndpoint>().Count(),
                Is.EqualTo(1));
            Assert.That(
                config.Dns.Servers.OfType<TailscaleDnsServer>().Count(),
                Is.EqualTo(1));
            Assert.That(
                config.Route.Rules.Any(rule =>
                    rule.PreferredBy?.Contains(
                        SingboxTags.TailscaleEndpoint) == true),
                Is.True);
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

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
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement serializedRuleSets = document.RootElement
            .GetProperty("route")
            .GetProperty("rule_set");
        JsonElement serializedGeositeTags = serializedRuleSets[0]
            .GetProperty("tag");

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
            Assert.That(json, Does.Contain(AdBlockingRuleSets.SagerAdsTag));
            Assert.That(json, Does.Not.Contain("anti-ad"));
            Assert.That(json, Does.Not.Contain("anti-ad.net"));
            Assert.That(json, Does.Not.Contain("adguard-dns"));
            Assert.That(serializedRuleSets.GetArrayLength(), Is.EqualTo(2));
            Assert.That(serializedGeositeTags.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(serializedGeositeTags.GetArrayLength(), Is.EqualTo(8));
            Assert.That(
                serializedRuleSets[0].GetProperty("url").GetString(),
                Is.EqualTo("https://fastly.jsdelivr.net/gh/SagerNet/sing-geosite@rule-set/{tag}.srs"));
            Assert.That(
                serializedRuleSets[1].GetProperty("tag").ValueKind,
                Is.EqualTo(JsonValueKind.String));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

    [Test]
    public void RouteUsesNativeIpVersionAndKeepsQuicRejectResponsesEnabled()
    {
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], [], []),
            TargetPlatform.Linux,
            new string('a', 64)));

        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"ip_version\": 6"));
            Assert.That(json, Does.Not.Contain("::/0"));
            Assert.That(
                json.Split("\"no_drop\": true", StringSplitOptions.None).Length - 1,
                Is.EqualTo(3));
            Assert.That(json, Does.Not.Contain("\"no_drop\": false"));
            Assert.That(json, Does.Not.Contain("\"invert\""));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void SingboxApiIsOptionalAndLoopbackOnly(TargetPlatform platform)
    {
        SingboxConfig disabled = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], [], []),
            platform,
            new string('a', 64)));
        SingboxConfig enabled = CreateBuilder(apiEnabled: true).Build(
            new SingboxBuildRequest(
                new NodeCatalog([], [], []),
                platform,
                new string('b', 64)));

        ApiService api = enabled.Services?.OfType<ApiService>().Single()
            ?? throw new AssertionException("缺少 sing-box API service");
        string disabledJson = new ConfigSerializer().Serialize(disabled);
        string enabledJson = new ConfigSerializer().Serialize(enabled);

        Assert.Multiple(() =>
        {
            Assert.That(disabled.Services, Is.Null);
            Assert.That(disabledJson, Does.Not.Contain("\"services\""));
            Assert.That(enabledJson, Does.Contain("\"type\": \"api\""));
            Assert.That(enabledJson, Does.Contain("\"listen_port\": 9090"));
            Assert.That(api.Tag, Is.EqualTo(SingboxTags.ApiService));
            Assert.That(api.Listen, Is.EqualTo("127.0.0.1"));
            Assert.That(api.ListenPort, Is.EqualTo(9090));
            Assert.That(api.AccessControlAllowOrigin, Does.Not.Contain("*"));
            Assert.That(api.AccessControlAllowPrivateNetwork, Is.False);
            Assert.That(api.Dashboard?.Enabled, Is.True);
            Assert.That(api.Dashboard?.Path, Is.EqualTo("dashboard"));
            Assert.That(
                api.Dashboard?.HttpClient,
                Is.EqualTo(HttpClientTags.RuleSetDirect));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(enabled));
    }

    [Test]
    public void ProxyServerDomainsUseFreshIpv4OnlyResolverObjects()
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
            Assert.That(
                generated.DomainResolver.DisableOptimisticCache,
                Is.True);
            Assert.That(json, Does.Contain("\"domain_resolver\": {"));
            Assert.That(json, Does.Contain("\"strategy\": \"ipv4_only\""));
            Assert.That(
                json,
                Does.Contain("\"disable_optimistic_cache\": true"));
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

    private static SingboxConfigBuilder CreateBuilder(
        TailscaleOptions? options = null,
        bool apiEnabled = false)
    {
        var tailscaleOptions = Options.Create(options ?? new TailscaleOptions());

        return new SingboxConfigBuilder(
            new TailscaleEndpointBuilder(tailscaleOptions),
            new DnsProfileBuilder(tailscaleOptions),
            new RouteProfileBuilder(tailscaleOptions),
            new SingboxApiServiceBuilder(Options.Create(
                new SingboxApiOptions { Enabled = apiEnabled })));
    }
}
