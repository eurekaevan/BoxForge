using System.Text.Json;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using BoxForge.Exceptions;
using System.Collections;
using BoxForge.Converters;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;
using BoxForge.Parsers;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SingboxConfigBuilderTests
{
    [TestCase("leaf", "IR002", "plan.groups.leaves[0]")]
    [TestCase("dns_detour", "IR003", "plan.dns.resolvers[0].detour")]
    [TestCase("dns_rule_match", "IR005", "dns_rule")]
    [TestCase("dns_rule_set", "IR005", "plan.dns.pools[0].rule_sets[0]")]
    [TestCase("service_selector", "IR003", "service_rule")]
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
        var plan = ConfigurationPlanner.Plan(nodes, CreateProfile(TargetPlatform.Linux, apiEnabled: true));
        if (fault == "service_selector")
            path = $"plan.route.rules[{plan.Route.Rules.Select((rule, index) => (rule, index)).First(item => item.rule.Phase == RoutePhase.PriorityServices).index}].outbound";
        int dnsRuleIndex = plan.Dns.Rules.Select((rule, index) => (rule, index)).First(item => !item.rule.RuleSet.IsDefaultOrEmpty).index;
        if (fault == "dns_rule_match")
            path = $"plan.dns.rules[{dnsRuleIndex}].rule_set[0]";
        plan = fault switch
        {
            "leaf" => plan with { Nodes = [] },
            "dns_detour" => plan with { Dns = plan.Dns with { Resolvers = [plan.Dns.Resolvers[0] with { Detour = "missing" }, .. plan.Dns.Resolvers.Skip(1)] } },
            "dns_rule_match" => plan with { Dns = plan.Dns with { Rules = plan.Dns.Rules.SetItem(dnsRuleIndex, plan.Dns.Rules[dnsRuleIndex] with { RuleSet = ["missing"] }) } },
            "dns_rule_set" => plan with { Dns = plan.Dns with { Pools = [plan.Dns.Pools[0] with { RuleSets = ["missing"] }, .. plan.Dns.Pools.Skip(1)] } },
            "service_selector" => plan with { Groups = plan.Groups with { Services = [] } },
            "route_final" => plan with { Route = plan.Route with { FinalOutbound = "missing" } },
            "resolver" => plan with { HttpClients = [plan.HttpClients[0] with { Resolver = plan.HttpClients[0].Resolver with { Server = "missing" } }, .. plan.HttpClients.Skip(1)] },
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
                    Rules = [.. plan.Dns.Rules.Select(ReplaceBootstrapResolver)],
                    Pools = [.. plan.Dns.Pools.Select(pool => pool with
                { PrimaryResolver = pool.PrimaryResolver == SingboxTags.DirectAliDns ? SingboxTags.DirectTencentDns : pool.PrimaryResolver })]
                }
            },
            _ => plan with { Dns = plan.Dns with { SelectionMode = DnsResolverSelectionMode.SequentialFallback } }
        };
        var error = Assert.Throws<ConfigValidationException>(() => plan.Validate());
        Assert.That(error!.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Path == path), Is.True);
        Assert.That(error.Diagnostics.All(diagnostic => diagnostic.Code.StartsWith("IR", StringComparison.Ordinal)), Is.True);

        static DnsRulePlan ReplaceBootstrapResolver(DnsRulePlan rule) => rule with
        {
            Server = rule.Server == SingboxTags.DirectAliDns ? SingboxTags.DirectTencentDns : rule.Server,
            Rules = rule.Rules.IsDefault ? default : [.. rule.Rules.Select(ReplaceBootstrapResolver)]
        };
    }

    [Test]
    public void ConfigurationPlanReportsAllBindingFailuresInStableOrder()
    {
        var plan = ConfigurationPlanner.Plan(new NodeCatalog([], []), CreateProfile(TargetPlatform.Linux, apiEnabled: true));
        plan = plan with
        {
            TailscaleEndpointEnabled = false,
            Route = plan.Route with { FinalOutbound = "missing-outbound" },
            Dns = plan.Dns with { BlockedRuleSets = ["missing-rule-set"] },
            HttpClients = [plan.HttpClients[0] with { Resolver = plan.HttpClients[0].Resolver with { Server = "missing-resolver" } }]
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
    public void PlanCollectsLocalAndCrossModuleErrorsAndSnapshotsDiagnostics()
    {
        var plan = ConfigurationPlanner.Plan(new NodeCatalog([], []), CreateProfile(TargetPlatform.Linux));
        plan = plan with
        {
            Groups = plan.Groups with { Main = plan.Groups.Main with { Preferred = "sensitive-group" } },
            Dns = plan.Dns with { Rules = default },
            Route = plan.Route with { Rules = default, FinalOutbound = "sensitive-outbound" }
        };
        var error = Assert.Throws<ConfigValidationException>(plan.Validate)!;
        Assert.That(error.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[]
        {
            ("IR010", "plan.groups.main.preferred"),
            ("IR008", "plan.dns.rules"),
            ("IR008", "plan.route.rules"),
            ("IR003", "plan.route.final_outbound")
        }));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("sensitive"));
        var mutable = error.Diagnostics.ToList();
        var snapshot = new ConfigValidationException(mutable);
        mutable.Clear();
        Assert.That(snapshot.Diagnostics, Is.EqualTo(error.Diagnostics));
        Assert.Throws<NotSupportedException>(() => ((IList<ConfigDiagnostic>)snapshot.Diagnostics).Clear());
    }

    [Test]
    public void NodeResolverDiagnosticsUseSortedIndicesInsteadOfUntrustedNames()
    {
        var plan = ConfigurationPlanner.Plan(new NodeCatalog([], []), CreateProfile(TargetPlatform.Linux));
        plan = plan with
        {
            NodeResolvers = System.Collections.Immutable.ImmutableDictionary<string, DomainResolverPlan>.Empty
                .Add("z-sensitive\nname", new DomainResolverPlan("sensitive-resolver", AddressFamilyPolicy.Auto, true))
                .Add("a-sensitive]name", new DomainResolverPlan("sensitive-resolver", AddressFamilyPolicy.Auto, true))
        };
        var error = Assert.Throws<ConfigValidationException>(plan.Validate)!;
        Assert.That(error.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[]
        {
            ("IR002", "plan.node_resolvers[0]"), ("IR006", "plan.node_resolvers[0].server"),
            ("IR002", "plan.node_resolvers[1]"), ("IR006", "plan.node_resolvers[1].server")
        }));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("sensitive"));
    }

    [Test]
    public void BuildValidatesTheCompletePlanBeforeEmission()
    {
        var nodes = new NodeCatalog([CreateProxy("actual", "node.example.com")], ["invented"]);
        var exception = Assert.Throws<ConfigValidationException>(() => new SingboxConfigBuilder().Build(
            ConfigurationPlanner.Plan(nodes, CreateProfile(TargetPlatform.Linux)), null));
        Assert.That(exception!.Diagnostics, Is.EqualTo(new[]
        {
            new ConfigDiagnostic("IR002", "plan.groups.leaves[0]", "引用了未声明的目标。"),
            new ConfigDiagnostic("IR003", "plan.http_clients[0].detour", "引用了未声明的目标。")
        }));
    }

    [TestCase("groups", "IR010", "plan.groups.main.preferred")]
    [TestCase("dns", "IR008", "plan.dns.rules")]
    [TestCase("route", "IR010", "plan.route.phases")]
    public void BuildRejectsInvalidComponentPlansBeforeMappingTargets(string component, string code, string path)
    {
        var plan = ConfigurationPlanner.Plan(new NodeCatalog([], []), CreateProfile(TargetPlatform.Linux));
        plan = component switch
        {
            "groups" => plan with { Groups = plan.Groups with { Main = plan.Groups.Main with { Preferred = "missing" } } },
            "dns" => plan with { Dns = plan.Dns with { Rules = default } },
            _ => plan with { Route = plan.Route with { Phases = [] } }
        };
        var error = Assert.Throws<ConfigValidationException>(() => new SingboxConfigBuilder().Build(plan, null))!;
        Assert.That(error.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[] { (code, path) }));
    }

    [Test]
    public void EndpointSnapshotPreservesEffectiveRequestTaildropDirectory()
    {
        var profile = BoxForgeProfile.Default;
        var endpointProfile = profile.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            platforms = new { Linux = new { features = new { tailscale = new { taildrop_directory = "EndpointTaildrop" } } } }
        })!.AsObject());
        var effective = endpointProfile.Resolve(TargetPlatform.Linux, JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { taildrop_directory = "RequestTaildrop" } }
        })!.AsObject());
        var builder = new SingboxConfigBuilder();
        var nodes = new NodeCatalog([], []);
        var plan = ConfigurationPlanner.Plan(nodes, effective);
        var config = builder.Build(plan, new string('a', 64));
        Assert.Multiple(() =>
        {
            Assert.That(plan.TaildropDirectory, Is.EqualTo("RequestTaildrop"));
            Assert.That(config.Endpoints!.OfType<TailscaleEndpoint>().Single().TaildropDirectory, Is.EqualTo("RequestTaildrop"));
            Assert.That(ConfigurationPlanner.Plan(nodes, profile.Resolve(TargetPlatform.Linux)).TaildropDirectory,
                Is.EqualTo(profile.Resolve(TargetPlatform.Linux).Settings.TaildropDirectory));
            Assert.That(endpointProfile.Resolve(TargetPlatform.Linux).Settings.TaildropDirectory, Is.EqualTo("EndpointTaildrop"));
        });
    }

    [Test]
    public void PeripheralEmissionUsesTheValidatedFeatureSnapshot()
    {
        var enabled = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = true }, api = new { enabled = true } }
        })!.AsObject());
        var disabled = enabled.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = false }, api = new { enabled = false } }
        })!.AsObject());
        var nodes = new NodeCatalog([], []);
        var plan = ConfigurationPlanner.Plan(nodes, enabled.Resolve(TargetPlatform.Linux));
        var endpoints = TailscaleEndpointBuilder.Emit(plan.TaildropDirectory, plan.TailscaleEndpointEnabled);
        var services = SingboxApiServiceBuilder.Emit(plan.ApiServiceEnabled);
        Assert.Multiple(() =>
        {
            Assert.That(endpoints.Single().Tag, Is.EqualTo(SingboxTags.TailscaleEndpoint));
            Assert.That(((ApiService)services.Single()).Dashboard!.HttpClient,
                Is.EqualTo(plan.HttpClients.Single(client => client.Name == HttpClientTags.DashboardDirect).Name));
            Assert.That(ConfigurationPlanner.Plan(nodes, disabled.Resolve(TargetPlatform.Linux)).TailscaleEndpointEnabled, Is.False);
            Assert.That(ConfigurationPlanner.Plan(nodes, disabled.Resolve(TargetPlatform.Linux)).ApiServiceEnabled, Is.False);
            Assert.That(plan.TailscaleEndpointEnabled, Is.True);
        });
    }

    [Test]
    public async Task SharedPlatformPlansEmitIndependentConfigurationsConcurrently()
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
        var catalog = NodeCatalogBuilder.Build(input);
        var serializer = new ConfigSerializer();
        TargetPlatform[] platforms = Enum.GetValues<TargetPlatform>();
        var plans = platforms.ToDictionary(platform => platform,
            platform => ConfigurationPlanner.Plan(catalog, BoxForgeProfile.Default.Resolve(platform)));
        var builder = new SingboxConfigBuilder();
        var expected = platforms.ToDictionary(platform => platform,
            platform => serializer.Serialize(builder.Build(plans[platform], new string('a', 64))));
        var generated = await Task.WhenAll(Enumerable.Range(0, 3).SelectMany(_ => platforms.Reverse().Select(platform =>
            Task.Run(() => (Platform: platform, Config: builder.Build(plans[platform], new string('a', 64)))))));
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
            Assert.That(serializer.Serialize(builder.Build(plans[platform], new string('a', 64))),
                Is.EqualTo(expected[platform]));
        foreach (ProxyNode node in catalog.Nodes)
        {
            Assert.That(node.Connection.ConnectTimeout, Is.Null);
            if (node is Hysteria2Node hysteria2)
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
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([], []), CreateProfile(platform),
            new string('a', 64));

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

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Auto));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllPlatformsEnableTailscaleByDefault(TargetPlatform platform)
    {
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([], []), CreateProfile(platform),
            new string('b', 64));

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

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Auto));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AllPlatformsCanDisableTailscaleExplicitly(TargetPlatform platform)
    {
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([], []), CreateProfile(platform, tailscaleEnabled: platform == TargetPlatform.Android,
            androidEnabled: platform != TargetPlatform.Android),
            new string('c', 64));

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

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Auto));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void RuleSetsUseAnIpv4OnlyProxyHttpClient(TargetPlatform platform)
    {
        ProxyNode first = CreateProxy("美国 01", "us-1.example.com");
        ProxyNode second = CreateProxy("美国 02", "us-2.example.com");
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog(
                [first, second],
                [first.Name, second.Name]), CreateProfile(platform, profile: BoxForgeProfile.Default.ApplyOverrides(
            JsonSerializer.SerializeToNode(new { dns = new { address_family = "Ipv4Only" } })!.AsObject())),
            new string('a', 64));

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
        JsonElement serializedAds = serializedRuleSets.EnumerateArray().Single(ruleSet =>
            ruleSet.GetProperty("tag").ValueKind == JsonValueKind.String
            && ruleSet.GetProperty("tag").GetString() == RuleSetTags.Ads);

        Assert.Multiple(() =>
        {
            Assert.That(config.HttpClients, Has.Count.EqualTo(1));
            Assert.That(proxyClient.Tag, Is.EqualTo(HttpClientTags.RuleSetProxy));
            Assert.That(proxyClient.Detour, Is.EqualTo(regionAuto.Tag));
            Assert.That(serializedClient.GetProperty("detour").GetString(),
                Is.EqualTo(regionAuto.Tag));
            Assert.That(regionAuto.Outbounds, Is.EqualTo(new[] { first.Name, second.Name }));
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
            Assert.That(serializedRuleSets.GetArrayLength(), Is.EqualTo(6));
            Assert.That(serializedDustinWinTags.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(serializedDustinWinTags.EnumerateArray().Select(tag => tag.GetString()),
                Is.EqualTo(new[] { "games", RuleSetTags.Cn, RuleSetTags.CnIp }));
            Assert.That(
                serializedRuleSets[0].GetProperty("url").GetString(),
                Is.EqualTo("https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs"));
            Assert.That(serializedAds.GetProperty("url").GetString(),
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
        ProxyNode node = CreateProxy("美国 01", "us-1.example.com");
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([node], [node.Name]), CreateProfile(TargetPlatform.Linux),
            new string('a', 64));

        Assert.Multiple(() =>
        {
            Assert.That(config.Outbounds.OfType<UrlTestOutbound>(), Is.Empty);
            Assert.That(config.HttpClients.Single().Detour, Is.EqualTo(node.Name));
            Assert.That(config.Route.DefaultHttpClient,
                Is.EqualTo(HttpClientTags.RuleSetProxy));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Auto));
    }

    [Test]
    public void DefaultRouteSerializesConditionalIpv6GuardsWithoutPortBasedQuicRejection()
    {
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([], []), CreateProfile(TargetPlatform.Linux),
            new string('a', 64));

        string json = new ConfigSerializer().Serialize(config);
        using JsonDocument document = JsonDocument.Parse(json);
        List<JsonElement> ipv6Rejects = document.RootElement.GetProperty("route")
            .GetProperty("rules").EnumerateArray()
            .Where(rule => rule.GetProperty("action").GetString() == "reject"
                && rule.TryGetProperty("rules", out JsonElement conditions)
                && conditions.EnumerateArray().Any(child => child.TryGetProperty("ip_version", out JsonElement version)
                    && version.GetInt32() == 6)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"ip_version\": 6"));
            Assert.That(
                json.Split("\"ip_version\": 6", StringSplitOptions.None).Length - 1,
                Is.EqualTo(2));
            Assert.That(json, Does.Not.Contain("::/0"));
            Assert.That(json, Does.Not.Contain("\"no_drop\""));
            Assert.That(document.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
                .Any(rule => rule.GetProperty("action").GetString() == "reject"
                    && rule.TryGetProperty("port", out JsonElement ports)
                    && ports.EnumerateArray().Any(port => port.GetInt32() == 443)), Is.False);
            Assert.That(ipv6Rejects, Has.Count.EqualTo(2));
            Assert.That(ipv6Rejects.All(rule =>
                rule.GetProperty("action").GetString() == "reject"
                && rule.GetProperty("rules")[1].GetProperty("default_interface_address")[0].GetString() == "2000::/3"
                && rule.GetProperty("rules")[1].GetProperty("invert").GetBoolean()
                && !rule.TryGetProperty("invert", out _)
                && !rule.TryGetProperty("inbound", out _)
                && !rule.TryGetProperty("network", out _)), Is.True);
            Assert.That(json, Does.Not.Contain("\"invert\": false"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Auto));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void SingboxApiIsOptionalAndLoopbackOnly(TargetPlatform platform)
    {
        SingboxConfig disabled = PlanAndBuild(
            new NodeCatalog([], []), CreateProfile(platform),
            new string('a', 64));
        SingboxConfig enabled = PlanAndBuild(
                new NodeCatalog([], []), CreateProfile(platform, apiEnabled: true),
                new string('b', 64));

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

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(enabled, AddressFamilyPolicy.Auto));
    }

    [Test]
    public void ProxyDomainsInheritFreshIpv4OnlyControlPlaneResolver()
    {
        var proxy = ProxyNodeParser.Parse(new ClashProxyNode(new Hashtable
        {
            ["name"] = "美国 01",
            ["type"] = "vless",
            ["server"] = "node.example.com",
            ["port"] = 443,
            ["uuid"] = "00000000-0000-4000-8000-000000000001"
        }));
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([proxy], [proxy.Name]), CreateProfile(TargetPlatform.Linux, profile: BoxForgeProfile.Default.ApplyOverrides(
            JsonSerializer.SerializeToNode(new { dns = new { address_family = "Ipv4Only" } })!.AsObject())),
            new string('b', 64));

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
        var source = (Hysteria2Node)ProxyNodeParser.Parse(new ClashProxyNode(new Hashtable
        {
            ["name"] = "HY2 node",
            ["type"] = "hysteria2",
            ["server"] = "node.example.com",
            ["port"] = 443,
            ["password"] = "test-only"
        }));
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([source], [source.Name]), CreateProfile(platform),
            new string('d', 64));
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
            Assert.That(source.Connection.ConnectTimeout, Is.Null);
            Assert.That(source.HopInterval, Is.Null);
        });
    }

    [Test]
    public void GoogleServiceDefaultsToUnitedStatesGroupWhenAvailable()
    {
        ProxyNode first = CreateProxy("美国 01", "us-1.example.com");
        ProxyNode second = CreateProxy("美国 02", "us-2.example.com");
        ProfilePlan plan = SingboxGroupEmitter.Emit(ProfilePlanner.PlanGroups(new NodeCatalog(
            [first, second],
            ["美国 01", "美国 02"]), BoxForgeProfile.Default.Resolve(TargetPlatform.Linux)));

        SelectorOutbound google = plan.ServiceOutbounds.Single(outbound =>
            outbound.Tag == "🔎 Google");
        string unitedStates = BoxForgeProfile.Default.Resolve(TargetPlatform.Linux).Regions.Single(region =>
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
        ProxyNode first = CreateProxy("香港 01", "hk-1.example.com");
        ProxyNode second = CreateProxy("香港 02", "hk-2.example.com");
        var nodes = new NodeCatalog(
            [first, second],
            [first.Name, second.Name]);
        ProfilePlan plan = SingboxGroupEmitter.Emit(ProfilePlanner.PlanGroups(nodes, BoxForgeProfile.Default.Resolve(TargetPlatform.Linux)));
        SingboxConfig config = PlanAndBuild(
            nodes, CreateProfile(TargetPlatform.Android),
            new string('d', 64));

        SelectorOutbound games = plan.ServiceOutbounds.Single(outbound =>
            outbound.Tag == "🎮 GAMES");
        string hongKong = BoxForgeProfile.Default.Resolve(TargetPlatform.Linux).Regions.Single(region =>
            region.Id == "hk").DisplayName;
        RouteRule gamesRoute = config.Route.Rules.Single(rule =>
            rule.Outbound == "🎮 GAMES");
        SingboxRuleSet dustinWin = config.Route.RuleSet.Single(ruleSet =>
            ruleSet.Tag?.Contains("games") == true);

        Assert.Multiple(() =>
        {
            Assert.That(games.Default, Is.EqualTo(hongKong));
            Assert.That(games.Outbounds, Does.Contain(hongKong));
            Assert.That(gamesRoute.RuleSet, Is.EqualTo(new[] { "games" }));
            Assert.That(dustinWin.Url,
                Is.EqualTo("https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs"));
            Assert.That(new ConfigSerializer().Serialize(config),
                Does.Not.Contain("Steam"));
        });

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Auto));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void GeneratedUrlTestsContainOnlyLeafNodesAndUsePlatformProbeInterval(TargetPlatform platform)
    {
        ProxyNode usFirst = CreateProxy("美国 01", "us-1.example.com");
        ProxyNode usSecond = CreateProxy("美国 02", "us-2.example.com");
        ProxyNode jpFirst = CreateProxy("日本 01", "jp-1.example.com");
        ProxyNode jpSecond = CreateProxy("日本 02", "jp-2.example.com");
        var nodes = new NodeCatalog(
            [usFirst, usSecond, jpFirst, jpSecond],
            [usFirst.Name, usSecond.Name, jpFirst.Name, jpSecond.Name]);

        SingboxConfig config = PlanAndBuild(
            nodes, CreateProfile(platform),
            new string('c', 64));
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

        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Auto));
    }

    [TestCase(AddressFamilyPolicy.DualStack)]
    [TestCase(AddressFamilyPolicy.Auto)]
    public void BuilderAndValidatorShareTheResolvedProfileAddressFamily(AddressFamilyPolicy addressFamily)
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            platforms = new { Linux = new { dns = new { address_family = addressFamily.ToString() } } }
        })!.AsObject());
        var resolved = profile.Resolve(TargetPlatform.Linux);
        SingboxConfig config = PlanAndBuild(
            new NodeCatalog([CreateProxy("ipv6-test", "2001:db8::1")], ["ipv6-test"]), resolved, new string('a', 64));
        Assert.That(config.Route.DefaultDomainResolver!.Strategy, Is.Null);
        Assert.That(config.Dns.Strategy, Is.Null);
        Assert.That(config.Dns.Rules.Any(rule => rule.QueryType?.Contains("AAAA") == true), Is.False);
        Assert.That(config.Route.Rules.Any(rule => rule.IpVersion == 6 && rule.Action == RouteRuleAction.Reject), Is.False);
        Assert.That(config.HttpClients.All(client => client.DomainResolver!.Strategy is null), Is.True);
        Assert.DoesNotThrow(() => new SingboxConfigValidator().Validate(config, resolved.Settings.AddressFamily));
        Assert.Throws<Exceptions.ConfigValidationException>(() => new SingboxConfigValidator().Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    private static SingboxConfig PlanAndBuild(NodeCatalog nodes, ResolvedProfile profile, string? cacheId) =>
        new SingboxConfigBuilder().Build(ConfigurationPlanner.Plan(nodes, profile), cacheId);

    private static ShadowsocksNode CreateProxy(string tag, string server) =>
        (ShadowsocksNode)ProxyNodeParser.Parse(new ClashProxyNode(new Hashtable
        {
            ["name"] = tag,
            ["type"] = "ss",
            ["server"] = server,
            ["port"] = 443,
            ["cipher"] = "aes-128-gcm",
            ["password"] = "test-only"
        }));

    private static ResolvedProfile CreateProfile(
        TargetPlatform platform,
        bool? tailscaleEnabled = null,
        bool? androidEnabled = null,
        bool? apiEnabled = null,
        BoxForgeProfile? profile = null)
    {
        profile ??= BoxForgeProfile.Default;
        if (tailscaleEnabled is not null || androidEnabled is not null || apiEnabled is not null)
            profile = profile.ApplyOverrides(JsonSerializer.SerializeToNode(new
            {
                features = new
                {
                    tailscale = new { enabled = tailscaleEnabled ?? profile.Features.Tailscale.Enabled },
                    api = new { enabled = apiEnabled ?? profile.Features.Api.Enabled }
                },
                platforms = new
                {
                    Android = new { features = new { tailscale = new { enabled = androidEnabled ?? profile.Resolve(TargetPlatform.Android).Features.Tailscale.Enabled } } }
                }
            })!.AsObject());
        return profile.Resolve(platform);
    }
}
