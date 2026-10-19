using System.Text.Json;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using BoxForge.Exceptions;
using Microsoft.Extensions.Options;
using BoxForge.Converters;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;
using BoxForge.Parsers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SingboxConfigBuilderTests
{
    [TestCase("leaf", "IR002", "plan.groups.leaves[0]")]
    [TestCase("dns_detour", "IR003", "plan.dns.resolvers[0].detour")]
    [TestCase("dns_rule_set", "IR005", "plan.dns.pools[0].rule_sets[0]")]
    [TestCase("service_selector", "IR003", "plan.route.priority_services[0].name")]
    [TestCase("route_final", "IR003", "plan.route.final_outbound")]
    [TestCase("resolver", "IR006", "plan.http_clients[0].resolver")]
    [TestCase("client_detour", "IR003", "plan.http_clients[0].detour")]
    [TestCase("download_client", "IR007", "plan.route.default_http_client")]
    [TestCase("dashboard_client", "IR007", "plan.services.dashboard.http_client")]
    [TestCase("endpoint", "IR004", "plan.dns.tailscale_enabled")]
    [TestCase("address_family", "IR001", "plan.dns.address_family")]
    [TestCase("forwarding", "IR001", "plan.route.use_direct_forwarding")]
    [TestCase("probe_interval", "IR001", "plan.groups.probes[0].interval")]
    [TestCase("bootstrap_resolver", "IR006", "plan.route.default_domain_resolver")]
    [TestCase("selection_mode", "IR001", "plan.dns.selection_mode")]
    public void ConfigurationPlanRejectsBrokenCrossModuleBindings(string fault, string code, string path)
    {
        var nodes = new NodeCatalog([CreateProxy("US01", "us-1.example.com"), CreateProxy("US02", "us-2.example.com")], ["US01", "US02"]);
        var plan = CreateBuilder(apiEnabled: true).Plan(new SingboxBuildRequest(nodes, TargetPlatform.Linux, new string('a', 64)));
        plan = fault switch
        {
            "leaf" => plan with { LeafTags = [] },
            "dns_detour" => plan with { Dns = plan.Dns with { Resolvers = [plan.Dns.Resolvers[0] with { Detour = "missing" }, .. plan.Dns.Resolvers.Skip(1)] } },
            "dns_rule_set" => plan with { Dns = plan.Dns with { Pools = [plan.Dns.Pools[0] with { RuleSets = ["missing"] }, .. plan.Dns.Pools.Skip(1)] } },
            "service_selector" => plan with { Groups = plan.Groups with { Services = [] } },
            "route_final" => plan with { Route = plan.Route with { FinalOutbound = "missing" } },
            "resolver" => plan with { HttpClients = [plan.HttpClients[0] with { Resolver = "missing" }, .. plan.HttpClients.Skip(1)] },
            "client_detour" => plan with { HttpClients = [plan.HttpClients[0] with { Detour = "missing" }, .. plan.HttpClients.Skip(1)] },
            "download_client" => plan with { HttpClients = [plan.HttpClients[1]] },
            "dashboard_client" => plan with { HttpClients = [plan.HttpClients[0]] },
            "endpoint" => plan with { TailscaleEndpointEnabled = false },
            "address_family" => plan with { Dns = plan.Dns with { AddressFamily = AddressFamilyPolicy.DualStack } },
            "forwarding" => plan with { Route = plan.Route with { UseDirectForwarding = false } },
            "probe_interval" => plan with { Groups = plan.Groups with { Probes = [plan.Groups.Probes[0] with { Interval = "9m" }] } },
            "bootstrap_resolver" => plan with
            {
                Dns = plan.Dns with
                {
                    Resolvers = [.. plan.Dns.Resolvers.Skip(1)],
                    Pools = [.. plan.Dns.Pools.Select(pool => pool with
                { PrimaryResolver = pool.PrimaryResolver == SingboxTags.DirectAliDns ? SingboxTags.DirectTencentDns : pool.PrimaryResolver })]
                }
            },
            _ => plan with { Dns = plan.Dns with { SelectionMode = DnsResolverSelectionMode.SequentialFallback } }
        };
        var error = Assert.Throws<ConfigValidationException>(() => plan.Validate());
        Assert.That(error!.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Path == path), Is.True);
        Assert.That(error.Diagnostics.All(diagnostic => diagnostic.Code.StartsWith("IR", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void ConfigurationPlanReportsAllBindingFailuresInStableOrder()
    {
        var plan = CreateBuilder(apiEnabled: true).Plan(new SingboxBuildRequest(
            new NodeCatalog([], []), TargetPlatform.Linux, new string('a', 64)));
        plan = plan with
        {
            TailscaleEndpointEnabled = false,
            Route = plan.Route with { FinalOutbound = "missing-outbound" },
            Dns = plan.Dns with { BlockedRuleSets = ["missing-rule-set"] },
            HttpClients = [plan.HttpClients[0] with { Resolver = "missing-resolver" }]
        };
        var first = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        var second = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        Assert.That(first.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Path)), Is.EqualTo(new[]
        {
            ("IR004", "plan.dns.tailscale_enabled"),
            ("IR004", "plan.route.tailscale_enabled"),
            ("IR003", "plan.route.final_outbound"),
            ("IR005", "plan.dns.blocked_rule_sets[0]"),
            ("IR006", "plan.http_clients[0].resolver"),
            ("IR007", "plan.services.dashboard.http_client")
        }));
        Assert.That(second.Diagnostics, Is.EqualTo(first.Diagnostics));
    }

    [Test]
    public void BuildRejectsLegacyCatalogWithUndeclaredPlannedLeaves()
    {
        var nodes = new NodeCatalog([CreateProxy("actual", "node.example.com")], ["invented"]);
        var exception = Assert.Throws<ConfigValidationException>(() => CreateBuilder().Build(
            new SingboxBuildRequest(nodes, TargetPlatform.Linux, new string('a', 64))));
        Assert.That(exception!.Diagnostics, Is.EqualTo(new[]
        {
            new ConfigDiagnostic("IR002", "plan.groups.leaves[0]", "引用了未声明的目标 'invented'。"),
            new ConfigDiagnostic("IR003", "plan.http_clients[0].detour", "引用了未声明的目标 'invented'。")
        }));
    }

    [Test]
    public void ConfigurationBindingsFollowAnExplicitlyModifiedCompatibilityView()
    {
        string yaml = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "tls.yaml"));
        var input = new ClashParser().Parse(yaml)!;
        var catalog = new NodeCatalogBuilder([new VlessConverter(), new TrojanConverter()],
            NullLogger<NodeCatalogBuilder>.Instance, BoxForgeProfile.Default).Build(input, strictNodeValidation: true);
        var view = (IList<ProxyOutbound>)catalog.Outbounds;
        int index = view.ToList().FindIndex(node => node is TrojanOutbound);
        string originalName = view[index].Tag;
        view[index] = ((TrojanOutbound)view[index]) with { Tag = "renamed" };
        catalog = catalog with { Names = [.. catalog.Names.Select(name => name == originalName ? "renamed" : name)] };
        var config = CreateBuilder().Build(new SingboxBuildRequest(catalog, TargetPlatform.Linux, new string('a', 64)));
        Assert.That(config.Outbounds.OfType<TrojanOutbound>().Single().Tag, Is.EqualTo("renamed"));
        Assert.That(config.Outbounds.OfType<SelectorOutbound>().First().Outbounds, Does.Contain("renamed"));
        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void EndpointSnapshotPreservesItsConfiguredTaildropDirectory()
    {
        var profile = BoxForgeProfile.Default;
        var endpointProfile = new BoxForgeProfile(BuiltInProfile.Default with
        {
            Platforms = BuiltInProfile.Default.Platforms.SetItem(TargetPlatform.Linux,
                profile.GetPlatform(TargetPlatform.Linux) with { TaildropDirectory = "EndpointTaildrop" })
        });
        var options = Options.Create(new TailscaleOptions());
        var builder = new SingboxConfigBuilder(new TailscaleEndpointBuilder(options, endpointProfile),
            new DnsProfileBuilder(options, profile), new RouteProfileBuilder(options, profile),
            new SingboxApiServiceBuilder(Options.Create(new SingboxApiOptions())), profile);
        var config = builder.Build(new SingboxBuildRequest(new NodeCatalog([], []), TargetPlatform.Linux, new string('a', 64)));
        Assert.That(config.Endpoints!.OfType<TailscaleEndpoint>().Single().TaildropDirectory, Is.EqualTo("EndpointTaildrop"));
    }

    [Test]
    public void PeripheralEmissionUsesTheValidatedFeatureSnapshot()
    {
        var tailscale = new TailscaleOptions { Enabled = true, AndroidEnabled = true };
        var api = new SingboxApiOptions { Enabled = true };
        var options = Options.Create(tailscale);
        var profile = BoxForgeProfile.Default;
        var builder = new SingboxConfigBuilder(new TailscaleEndpointBuilder(options, profile),
            new DnsProfileBuilder(options, profile), new RouteProfileBuilder(options, profile),
            new SingboxApiServiceBuilder(Options.Create(api)), profile);
        var request = new SingboxBuildRequest(new NodeCatalog([], []), TargetPlatform.Linux, new string('a', 64));
        var plan = builder.Plan(request);
        tailscale.Enabled = tailscale.AndroidEnabled = api.Enabled = false;
        var endpoints = TailscaleEndpointBuilder.Build(plan.TaildropDirectory, plan.TailscaleEndpointEnabled);
        var services = SingboxApiServiceBuilder.Build(plan.ApiServiceEnabled);
        Assert.Multiple(() =>
        {
            Assert.That(endpoints.Single().Tag, Is.EqualTo(SingboxTags.TailscaleEndpoint));
            Assert.That(((ApiService)services.Single()).Dashboard!.HttpClient,
                Is.EqualTo(plan.HttpClients.Single(client => client.Name == HttpClientTags.DashboardDirect).Name));
            Assert.That(builder.Plan(request).TailscaleEndpointEnabled, Is.False);
            Assert.That(builder.Plan(request).ApiServiceEnabled, Is.False);
        });
    }

    [TestCase("untouched")]
    [TestCase("read")]
    [TestCase("hash")]
    [TestCase("equality")]
    [TestCase("copy-hash")]
    [TestCase("legacy-constructor")]
    [TestCase("converted-nodes")]
    public async Task SharedCatalogGeneratesIndependentPlatformConfigurationsConcurrently(string access)
    {
        var input = new ClashConfig { Proxies = [] };
        var parser = new ClashParser();
        foreach (string fixture in new[] { "tls", "hysteria2", "anytls", "shadowsocks" })
        {
            string yaml = await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fixture + ".yaml"));
            if (fixture is "hysteria2" or "anytls")
                yaml = yaml.Replace("    password: test-only", "    password: test-only\n    alpn: [h2, http/1.1]", StringComparison.Ordinal);
            input.Proxies.AddRange(parser.Parse(yaml)!.Proxies);
        }
        var catalog = new NodeCatalogBuilder(
            [new VlessConverter(), new TrojanConverter(), new Hysteria2Converter(), new AnyTlsConverter(), new ShadowsocksConverter()],
            NullLogger<NodeCatalogBuilder>.Instance, BoxForgeProfile.Default).Build(input, strictNodeValidation: true);
        var builder = CreateBuilder();
        var serializer = new ConfigSerializer();
        TargetPlatform[] platforms = Enum.GetValues<TargetPlatform>();
        var expected = platforms.ToDictionary(platform => platform,
            platform => serializer.Serialize(builder.Build(new SingboxBuildRequest(catalog, platform, new string('a', 64)))));
        switch (access)
        {
            case "read":
                _ = catalog.Outbounds;
                break;
            case "hash":
                _ = catalog.GetHashCode();
                break;
            case "equality":
                Assert.That(catalog.Equals(catalog with { }), Is.True);
                break;
            case "copy-hash":
                _ = (catalog with { }).GetHashCode();
                break;
            case "legacy-constructor":
                catalog = new NodeCatalog(catalog.Outbounds, catalog.Names);
                break;
            case "converted-nodes":
                catalog = NodeCatalog.FromEntries(catalog.Outbounds.Select(NodeConversionResult.Success).ToArray(), catalog.Names);
                break;
        }
        var generated = await Task.WhenAll(Enumerable.Range(0, 3).SelectMany(_ => platforms.Reverse().Select(platform =>
            Task.Run(() => (Platform: platform, Config: builder.Build(new SingboxBuildRequest(catalog, platform, new string('a', 64))))))));
        foreach (var result in generated)
            Assert.That(serializer.Serialize(result.Config), Is.EqualTo(expected[result.Platform]));
        foreach (ProxyOutbound outbound in generated[0].Config.Outbounds.OfType<ProxyOutbound>())
        {
            OutboundTls? tls = outbound switch
            {
                VlessOutbound vless => vless.Tls,
                TrojanOutbound trojan => trojan.Tls,
                Hysteria2Outbound hysteria2 => hysteria2.Tls,
                AnyTlsOutbound anyTls => anyTls.Tls,
                _ => null
            };
            tls?.Alpn?.Clear();
        }
        generated[0].Config.Outbounds.OfType<Hysteria2Outbound>().Single(node => node.Tag == "hy2-hop").ServerPorts!.Clear();
        foreach (var result in generated.Skip(1))
            Assert.That(serializer.Serialize(result.Config), Is.EqualTo(expected[result.Platform]));
        foreach (TargetPlatform platform in platforms)
            Assert.That(serializer.Serialize(builder.Build(new SingboxBuildRequest(catalog, platform, new string('a', 64)))),
                Is.EqualTo(expected[platform]));
        foreach (ParsedNode entry in (catalog.Entries ?? []).OfType<ParsedNode>())
        {
            Assert.That(entry.Node.Connection.ConnectTimeout, Is.Null);
            if (entry.Node is Hysteria2Node hysteria2)
            {
                Assert.That(hysteria2.TuningHopInterval, Is.Null);
                Assert.That(hysteria2.TuningBbrProfile, Is.Null);
            }
        }
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllPlatformsGenerateOnlyTunInbound(
        TargetPlatform platform)
    {
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            new NodeCatalog([], []),
            platform,
            new string('a', 64)));

        Inbound tunInbound = config.Inbounds.Single(inbound =>
            inbound.Tag == SingboxTags.TunInbound);
        bool hasBridge = config.Outbounds
            .OfType<BridgeOutbound>()
            .Any(outbound => outbound.Tag == SingboxTags.BridgeOutbound);
        string json = new ConfigSerializer().Serialize(config);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement serializedTun = document.RootElement.GetProperty("inbounds")
            .EnumerateArray().Single(inbound => inbound.GetProperty("type").GetString() == "tun");

        Assert.Multiple(() =>
        {
            Assert.That(config.Inbounds, Has.Count.EqualTo(1));
            Assert.That(tunInbound.Type, Is.EqualTo("tun"));
            Assert.That(json, Does.Not.Contain("mixed-in"));
            Assert.That(tunInbound.Address,
                Is.EqualTo(new[] { "172.19.0.1/30", "fd00::1/126" }));
            Assert.That(
                hasBridge,
                Is.EqualTo(platform == TargetPlatform.Linux));
            Assert.That(json, Does.Not.Contain("\"set_system_proxy\""));
            Assert.That(json, Does.Not.Contain("\"platform\""));
            Assert.That(json, Does.Not.Contain("\"http_proxy\""));
            Assert.That(json, Does.Not.Contain("\"mtu\""));
            Assert.That(json, Does.Not.Contain("\"stack\""));
            Assert.That(tunInbound.MultiQueue, Is.EqualTo(platform == TargetPlatform.Linux ? (bool?)true : null));
            if (platform == TargetPlatform.Linux)
            {
                Assert.That(serializedTun.GetProperty("multi_queue").GetBoolean(), Is.True);
            }
            else
            {
                Assert.That(serializedTun.TryGetProperty("multi_queue", out _), Is.False);
            }
            Assert.That(tunInbound.StrictRoute, Is.EqualTo(platform == TargetPlatform.Android ? (bool?)null : true));
            Assert.That(json.Contains("\"strict_route\"", StringComparison.Ordinal), Is.EqualTo(platform != TargetPlatform.Android));
            Assert.That(
                json.Contains("\"type\": \"bridge\"", StringComparison.Ordinal),
                Is.EqualTo(platform == TargetPlatform.Linux));
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
            Assert.That(serializedRuleSets.GetArrayLength(), Is.EqualTo(4));
            Assert.That(serializedDustinWinTags.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(serializedDustinWinTags.GetArrayLength(), Is.EqualTo(5));
            Assert.That(
                serializedRuleSets[0].GetProperty("url").GetString(),
                Is.EqualTo("https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs"));
            Assert.That(
                serializedRuleSets[1].GetProperty("tag").ValueKind,
                Is.EqualTo(JsonValueKind.String));
            Assert.That(serializedRuleSets[1].GetProperty("tag").GetString(),
                Is.EqualTo(RuleSetTags.Ads));
            Assert.That(serializedRuleSets[1].GetProperty("url").GetString(),
                Is.EqualTo("https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/sing/geo/geosite/category-ads-and-telemetry.srs"));
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
        using JsonDocument document = JsonDocument.Parse(json);
        List<JsonElement> ipv6Rejects = document.RootElement.GetProperty("route")
            .GetProperty("rules").EnumerateArray()
            .Where(rule => rule.TryGetProperty("ip_version", out JsonElement version)
                && version.GetInt32() == 6).ToList();

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
            Assert.That(ipv6Rejects, Has.Count.EqualTo(2));
            Assert.That(ipv6Rejects.All(rule =>
                rule.GetProperty("action").GetString() == "reject"
                && !rule.TryGetProperty("invert", out _)
                && !rule.TryGetProperty("inbound", out _)
                && !rule.TryGetProperty("network", out _)), Is.True);
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
            ["美国 01", "美国 02"]), BoxForgeProfile.Default);

        SelectorOutbound google = plan.ServiceOutbounds.Single(outbound =>
            outbound.Tag == ServiceGroupNames.Google);
        string unitedStates = ProfileDefinitions.Regions.Single(region =>
            region.Id == "us").DisplayName;

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
        ProfilePlan plan = ProfilePlanner.Plan(nodes, BoxForgeProfile.Default);
        SingboxConfig config = CreateBuilder().Build(new SingboxBuildRequest(
            nodes,
            TargetPlatform.Android,
            new string('d', 64)));

        SelectorOutbound games = plan.ServiceOutbounds.Single(outbound =>
            outbound.Tag == ServiceGroupNames.Games);
        string hongKong = ProfileDefinitions.Regions.Single(region =>
            region.Id == "hk").DisplayName;
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

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void GeneratedUrlTestsContainOnlyLeafNodesAndUsePlatformProbeInterval(TargetPlatform platform)
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
            platform,
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
            Assert.That(urlTests.Select(outbound => outbound.Interval),
                Is.All.EqualTo(platform == TargetPlatform.Android ? "3m" : "1m"));
            foreach (JsonElement outbound in serializedUrlTests)
            {
                Assert.That(outbound.GetProperty("interval").GetString(),
                    Is.EqualTo(platform == TargetPlatform.Android ? "3m" : "1m"));
            }
            Assert.That(
                urlTests.SelectMany(urlTest => urlTest.Outbounds),
                Is.All.Matches<string>(leafTags.Contains));
            Assert.That(
                serializedUrlTests.All(outbound =>
                    outbound.GetProperty("url").GetString() == "https://www.gstatic.com/generate_204"
                    && outbound.GetProperty("tolerance").GetInt32() == 50
                    && outbound.GetProperty("idle_timeout").GetString() == "30m"
                    && !outbound.GetProperty("interrupt_exist_connections").GetBoolean()),
                Is.True);
            Assert.That(
                config.Outbounds.OfType<SelectorOutbound>()
                    .All(selector => selector.InterruptExistConnections == true),
                Is.True);
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void BuilderAndValidatorShareTheInjectedProfileAddressFamily()
    {
        var profile = new BoxForgeProfile(BuiltInProfile.Default with
        {
            Platforms = BuiltInProfile.Default.Platforms.SetItem(TargetPlatform.Linux,
                BuiltInProfile.Default.Platforms[TargetPlatform.Linux] with
                { AddressFamily = AddressFamilyPolicy.DualStack })
        });
        var request = new SingboxBuildRequest(
            new NodeCatalog([CreateProxy("ipv6-test", "2001:db8::1")], ["ipv6-test"]),
            TargetPlatform.Linux, new string('a', 64));
        SingboxConfig config = CreateBuilder(profile: profile).Build(request);
        Assert.That(config.Route.DefaultDomainResolver!.Strategy, Is.Null);
        Assert.That(config.Dns.Strategy, Is.Null);
        Assert.That(config.Dns.Rules.Any(rule => rule.QueryType?.Contains("AAAA") == true), Is.False);
        Assert.That(config.Route.Rules.Any(rule => rule.IpVersion == 6 && rule.Action == RouteRuleAction.Reject), Is.False);
        Assert.That(config.HttpClients.All(client => client.DomainResolver!.Strategy is null), Is.True);
        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, profile.GetPlatform(request.Platform).AddressFamily));
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
        bool apiEnabled = false,
        BoxForgeProfile? profile = null)
    {
        profile ??= BoxForgeProfile.Default;
        var tailscaleOptions = Options.Create(options ?? new TailscaleOptions());

        return new SingboxConfigBuilder(
            new TailscaleEndpointBuilder(tailscaleOptions, profile),
            new DnsProfileBuilder(tailscaleOptions, profile),
            new RouteProfileBuilder(tailscaleOptions, profile),
            new SingboxApiServiceBuilder(Options.Create(
                new SingboxApiOptions { Enabled = apiEnabled })), profile);
    }
}
