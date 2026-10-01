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
            new NodeCatalog([], []),
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
            Assert.That(tunInbound.Address,
                Is.EqualTo(new[] { "172.19.0.1/30", "fd00::1/126" }));
            Assert.That(mixedInbound.Type, Is.EqualTo("mixed"));
            Assert.That(mixedInbound.Listen, Is.EqualTo("127.0.0.1"));
            Assert.That(mixedInbound.ListenPort, Is.EqualTo(8848));
            Assert.That(
                hasBridge,
                Is.EqualTo(platform != TargetPlatform.Android));
            Assert.That(json, Does.Not.Contain("\"set_system_proxy\""));
            Assert.That(json, Does.Not.Contain("\"platform\""));
            Assert.That(json, Does.Not.Contain("\"http_proxy\""));
            Assert.That(json, Does.Not.Contain("\"mtu\""));
            Assert.That(json, Does.Not.Contain("\"stack\""));
            Assert.That(tunInbound.StrictRoute, Is.EqualTo(platform == TargetPlatform.Android ? (bool?)null : true));
            Assert.That(json.Contains("\"strict_route\"", StringComparison.Ordinal), Is.EqualTo(platform != TargetPlatform.Android));
            Assert.That(
                json.Contains("\"type\": \"bridge\"", StringComparison.Ordinal),
                Is.EqualTo(platform != TargetPlatform.Android));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllPlatformsEnableTailscaleByDefault(TargetPlatform platform)
    {
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], []),
            platform,
            new string('b', 64)));

        TailscaleEndpoint? endpoint = config.Endpoints?
            .OfType<TailscaleEndpoint>()
            .SingleOrDefault();
        bool hasDnsServer = config.Dns.Servers
            .OfType<TailscaleDnsServer>()
            .Any();
        bool hasDirectBootstrap = config.Dns.Servers.Any(server =>
            server.Tag == SingboxTags.DirectAliDns);
        bool hasRoute = config.Route.Rules.Any(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleEndpoint) == true);
        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Is.Not.Null);
            Assert.That(endpoint!.OnDemand, Is.True);
            Assert.That(hasDnsServer, Is.True);
            Assert.That(hasDirectBootstrap, Is.True);
            Assert.That(config.Route.DefaultDomainResolver?.Server,
                Is.EqualTo(SingboxTags.DirectAliDns));
            Assert.That(hasRoute, Is.True);
            Assert.That(json, Does.Contain("\"on_demand\": true"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllPlatformsCanDisableTailscaleExplicitly(TargetPlatform platform)
    {
        var options = new TailscaleOptions();
        if (platform == TargetPlatform.Android)
        {
            options.AndroidEnabled = false;
        }
        else
        {
            options.Enabled = false;
        }

        SingboxConfig config = CreateBuilder(options).Build(new SingboxBuildRequest(
            new NodeCatalog([], []),
            platform,
            new string('c', 64)));

        Assert.Multiple(() =>
        {
            Assert.That(config.Endpoints, Is.Null);
            Assert.That(
                config.Dns.Servers.OfType<TailscaleDnsServer>().Count(),
                Is.EqualTo(0));
            Assert.That(
                config.Route.Rules.Any(rule =>
                    rule.PreferredBy?.Contains(
                        SingboxTags.TailscaleEndpoint) == true),
                Is.False);
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void RuleSetsUseAnIpv4OnlyProxyHttpClient(TargetPlatform platform)
    {
        ProxyOutbound first = CreateProxy("美国 01", "us-1.example.com");
        ProxyOutbound second = CreateProxy("美国 02", "us-2.example.com");
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog(
                [first, second],
                [first.Tag, second.Tag]),
            platform,
            new string('a', 64)));

        HttpClientConfig proxyClient = config.HttpClients.Single();
        UrlTestOutbound regionAuto = config.Outbounds.OfType<UrlTestOutbound>().Single();

        string json = new ConfigSerializer().Serialize(config);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement serializedClient = document.RootElement
            .GetProperty("http_clients")[0];
        JsonElement serializedRuleSets = document.RootElement
            .GetProperty("route")
            .GetProperty("rule_set");
        JsonElement serializedDustinWinTags = serializedRuleSets[0]
            .GetProperty("tag");

        Assert.Multiple(() =>
        {
            Assert.That(config.HttpClients, Has.Count.EqualTo(1));
            Assert.That(proxyClient.Tag, Is.EqualTo(HttpClientTags.RuleSetProxy));
            Assert.That(proxyClient.Detour, Is.EqualTo(regionAuto.Tag));
            Assert.That(serializedClient.GetProperty("detour").GetString(),
                Is.EqualTo(regionAuto.Tag));
            Assert.That(regionAuto.Outbounds, Is.EqualTo(new[] { first.Tag, second.Tag }));
            Assert.That(
                proxyClient.DomainResolver?.Server,
                Is.EqualTo(SingboxTags.DirectAliDns));
            Assert.That(
                proxyClient.DomainResolver?.Strategy,
                Is.EqualTo(DnsStrategy.Ipv4Only));
            Assert.That(
                config.Route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetProxy));
            Assert.That(
                config.Route.RuleSet.All(ruleSet => ruleSet.HttpClient == null),
                Is.True);
            Assert.That(json, Does.Not.Contain("\"http_client\":"));
            Assert.That(json, Does.Not.Contain("\"http_client\": null"));
            Assert.That(json, Does.Contain(RuleSetTags.Ads));
            Assert.That(json, Does.Not.Contain("anti-ad"));
            Assert.That(json, Does.Not.Contain("anti-ad.net"));
            Assert.That(json, Does.Not.Contain("adguard-dns"));
            Assert.That(serializedRuleSets.GetArrayLength(), Is.EqualTo(3));
            Assert.That(serializedDustinWinTags.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(serializedDustinWinTags.GetArrayLength(), Is.EqualTo(6));
            Assert.That(
                serializedRuleSets[0].GetProperty("url").GetString(),
                Is.EqualTo("https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs"));
            Assert.That(
                serializedRuleSets[1].GetProperty("tag").ValueKind,
                Is.EqualTo(JsonValueKind.String));
            Assert.That(json, Does.Not.Contain("SagerNet"));
            Assert.That(json, Does.Not.Contain("geosite-"));
            Assert.That(json, Does.Not.Contain("geoip-"));
            Assert.That(json, Does.Not.Contain("category-pt"));
            Assert.That(json, Does.Not.Contain("Steam"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void RuleSetDownloadsUseLeafNodeWhenRegionAutoIsUnavailable()
    {
        ProxyOutbound node = CreateProxy("美国 01", "us-1.example.com");
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([node], [node.Tag]),
            TargetPlatform.Linux,
            new string('a', 64)));

        Assert.Multiple(() =>
        {
            Assert.That(config.Outbounds.OfType<UrlTestOutbound>(), Is.Empty);
            Assert.That(config.HttpClients.Single().Detour, Is.EqualTo(node.Tag));
            Assert.That(config.Route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetProxy));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void RouteSerializesGlobalIpv6RejectAndQuicRejectSemantics()
    {
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], []),
            TargetPlatform.Linux,
            new string('a', 64)));

        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"ip_version\": 6"));
            Assert.That(
                json.Split("\"ip_version\": 6", StringSplitOptions.None).Length - 1,
                Is.EqualTo(2));
            Assert.That(json, Does.Not.Contain("::/0"));
            Assert.That(
                json.Split("\"no_drop\": true", StringSplitOptions.None).Length - 1,
                Is.EqualTo(3));
            Assert.That(json, Does.Not.Contain("\"no_drop\": false"));
            Assert.That(json, Does.Not.Contain("\"invert\": true"));
            Assert.That(json, Does.Not.Contain("\"invert\": false"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void SingboxApiIsOptionalAndLoopbackOnly(TargetPlatform platform)
    {
        SingboxConfig disabled = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], []),
            platform,
            new string('a', 64)));
        SingboxConfig enabled = CreateBuilder(apiEnabled: true).Build(
            new SingboxBuildRequest(
                new NodeCatalog([], []),
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
                Is.EqualTo(HttpClientTags.DashboardDirect));
            Assert.That(disabled.HttpClients, Has.Count.EqualTo(1));
            Assert.That(enabled.HttpClients, Has.Count.EqualTo(2));
            Assert.That(enabled.HttpClients.Single(client =>
                client.Tag == HttpClientTags.DashboardDirect).Detour, Is.Null);
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(enabled, AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void ProxyDomainsInheritFreshIpv4OnlyControlPlaneResolver()
    {
        var proxy = new VlessOutbound
        {
            Tag = "美国 01",
            Server = "node.example.com",
            ServerPort = 443,
            Uuid = "00000000-0000-4000-8000-000000000001"
        };
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([proxy], [proxy.Tag]),
            TargetPlatform.Linux,
            new string('b', 64)));

        string json = new ConfigSerializer().Serialize(config);

        Assert.Multiple(() =>
        {
            Assert.That(
                config.Route.DefaultDomainResolver?.Server,
                Is.EqualTo(SingboxTags.DirectAliDns));
            Assert.That(
                config.Route.DefaultDomainResolver?.Strategy,
                Is.EqualTo(DnsStrategy.Ipv4Only));
            Assert.That(
                config.Route.DefaultDomainResolver?.DisableOptimisticCache,
                Is.True);
            Assert.That(json, Does.Contain("\"default_domain_resolver\": {"));
            Assert.That(config.Dns.Rules.Any(rule =>
                rule.Domain?.Contains(proxy.Server) == true), Is.False);
            Assert.That(json, Does.Contain("\"strategy\": \"ipv4_only\""));
            Assert.That(
                json,
                Does.Contain("\"disable_optimistic_cache\": true"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [TestCase(TargetPlatform.Android, null, null)]
    [TestCase(TargetPlatform.Linux, "1m", "30s")]
    [TestCase(TargetPlatform.Windows, "1m", "30s")]
    public void BuilderAppliesOutboundTuningAfterConversion(
        TargetPlatform platform,
        string? keepAlive,
        string? keepAliveInterval)
    {
        var source = new Hysteria2Outbound
        {
            Tag = "HY2 node",
            Server = "node.example.com",
            ServerPort = 443,
            Password = "test-only"
        };
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([source], [source.Tag]),
            platform,
            new string('d', 64)));
        Hysteria2Outbound outbound = config.Outbounds
            .OfType<Hysteria2Outbound>()
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(outbound.ConnectTimeout, Is.EqualTo("5s"));
            Assert.That(outbound.TcpKeepAlive, Is.EqualTo(keepAlive));
            Assert.That(outbound.TcpKeepAliveInterval,
                Is.EqualTo(keepAliveInterval));
            Assert.That(outbound.HopInterval, Is.EqualTo("30s"));
            Assert.That(outbound.HopIntervalMax, Is.EqualTo("60s"));
            Assert.That(outbound.BbrProfile, Is.EqualTo("standard"));
            Assert.That(source.ConnectTimeout, Is.Null);
            Assert.That(source.HopInterval, Is.Null);
        });
    }

    [Test]
    public void GoogleServiceDefaultsToUnitedStatesGroupWhenAvailable()
    {
        ProxyOutbound first = CreateProxy("美国 01", "us-1.example.com");
        ProxyOutbound second = CreateProxy("美国 02", "us-2.example.com");
        ProfilePlan plan = ProfilePlanner.Plan(new NodeCatalog(
            [first, second],
            ["美国 01", "美国 02"]));

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

    [Test]
    public void GamesServiceUsesDustinWinRuleSetAndDefaultsToHongKong()
    {
        ProxyOutbound first = CreateProxy("香港 01", "hk-1.example.com");
        ProxyOutbound second = CreateProxy("香港 02", "hk-2.example.com");
        var nodes = new NodeCatalog(
            [first, second],
            [first.Tag, second.Tag]);
        ProfilePlan plan = ProfilePlanner.Plan(nodes);
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            nodes,
            TargetPlatform.Android,
            new string('d', 64)));

        SelectorOutbound games = plan.ServiceOutbounds.Single(outbound =>
            outbound.Tag == ServiceGroupNames.Games);
        string hongKong = ProfileDefinitions.Regions.Single(region =>
            region.Id == RegionId.HongKong).DisplayName;
        RouteRule gamesRoute = config.Route.Rules.Single(rule =>
            rule.Outbound == ServiceGroupNames.Games);
        SingboxRuleSet dustinWin = config.Route.RuleSet.Single(ruleSet =>
            ruleSet.Tag?.Contains(RuleSetTags.Games) == true);

        Assert.Multiple(() =>
        {
            Assert.That(games.Default, Is.EqualTo(hongKong));
            Assert.That(games.Outbounds, Does.Contain(hongKong));
            Assert.That(gamesRoute.RuleSet, Is.EqualTo(new[] { RuleSetTags.Games }));
            Assert.That(dustinWin.Url,
                Is.EqualTo("https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs"));
            Assert.That(new ConfigSerializer().Serialize(config),
                Does.Not.Contain("Steam"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void GeneratedUrlTestsContainOnlyLeafNodesAndOmitOfficialDefaults()
    {
        ProxyOutbound usFirst = CreateProxy("美国 01", "us-1.example.com");
        ProxyOutbound usSecond = CreateProxy("美国 02", "us-2.example.com");
        ProxyOutbound jpFirst = CreateProxy("日本 01", "jp-1.example.com");
        ProxyOutbound jpSecond = CreateProxy("日本 02", "jp-2.example.com");
        var nodes = new NodeCatalog(
            [usFirst, usSecond, jpFirst, jpSecond],
            [usFirst.Tag, usSecond.Tag, jpFirst.Tag, jpSecond.Tag]);

        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            nodes,
            TargetPlatform.Linux,
            new string('c', 64)));
        HashSet<string> leafTags = config.Outbounds
            .OfType<ProxyOutbound>()
            .Select(outbound => outbound.Tag)
            .ToHashSet(StringComparer.Ordinal);
        List<UrlTestOutbound> urlTests = config.Outbounds
            .OfType<UrlTestOutbound>()
            .ToList();
        string json = new ConfigSerializer().Serialize(config);
        using JsonDocument document = JsonDocument.Parse(json);
        List<JsonElement> serializedUrlTests = document.RootElement
            .GetProperty("outbounds")
            .EnumerateArray()
            .Where(outbound => outbound.GetProperty("type").GetString() == "urltest")
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(urlTests, Has.Count.EqualTo(2));
            Assert.That(
                urlTests.SelectMany(urlTest => urlTest.Outbounds),
                Is.All.Matches<string>(leafTags.Contains));
            Assert.That(
                serializedUrlTests.All(outbound =>
                    !outbound.TryGetProperty("url", out _)
                    && !outbound.TryGetProperty("interval", out _)
                    && !outbound.TryGetProperty("tolerance", out _)
                    && !outbound.TryGetProperty("idle_timeout", out _)
                    && !outbound.TryGetProperty(
                        "interrupt_exist_connections",
                        out _)),
                Is.True);
            Assert.That(
                config.Outbounds.OfType<SelectorOutbound>()
                    .All(selector => selector.InterruptExistConnections == true),
                Is.True);
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void BuilderAndValidatorShareTheExplicitRequestAddressFamily()
    {
        var request = new SingboxBuildRequest(
            new NodeCatalog([CreateProxy("ipv6-test", "2001:db8::1")], ["ipv6-test"]),
            TargetPlatform.Linux, new string('a', 64))
        { AddressFamily = AddressFamilyPolicy.DualStack };
        SingboxConfig config = CreateBuilder().Build(request);
        Assert.That(config.Route.DefaultDomainResolver!.Strategy, Is.Null);
        Assert.That(config.Dns.Strategy, Is.Null);
        Assert.That(config.Dns.Rules.Any(rule => rule.QueryType?.Contains("AAAA") == true), Is.False);
        Assert.That(config.Route.Rules.Any(rule => rule.IpVersion == 6 && rule.Action == RouteRuleAction.Reject), Is.False);
        Assert.That(config.HttpClients.All(client => client.DomainResolver!.Strategy is null), Is.True);
        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, request.AddressFamily));
        Assert.Throws<Exceptions.ConfigValidationException>(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    private static ShadowsocksOutbound CreateProxy(string tag, string server) =>
        new()
        {
            Tag = tag,
            Server = server,
            ServerPort = 443,
            Method = "aes-128-gcm",
            Password = "test-only"
        };

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
