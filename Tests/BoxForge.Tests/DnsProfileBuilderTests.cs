using BoxForge.Exceptions;
using BoxForge.Models.Intermediate;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Tests;

[TestFixture]
public sealed class DnsProfileBuilderTests
{
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.Ipv4Only)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.Ipv4Only)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.Ipv4Only)]
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.DualStack)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.DualStack)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.DualStack)]
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.Auto)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.Auto)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.Auto)]
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.DirectDualStack)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.DirectDualStack)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.DirectDualStack)]
    public void DnsPlanSnapshotsPolicyAndEmitsIndependentConfigurations(TargetPlatform platform, AddressFamilyPolicy addressFamily)
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            platforms = new Dictionary<string, object>
            {
                [platform.ToString()] = new { dns = new { address_family = addressFamily.ToString() } }
            }
        })!.AsObject());
        var settings = profile.Resolve(platform).Settings;
        var plan = DnsProfileBuilder.Plan(profile.Resolve(platform));
        string expected = JsonSerializer.Serialize(SingboxDnsEmitter.Emit(DnsProfileBuilder.Plan(profile.Resolve(platform))));
        var first = SingboxDnsEmitter.Emit(plan);
        Assert.That(JsonSerializer.Serialize(first), Is.EqualTo(expected));
        first.Servers.Clear();
        foreach (DnsRule rule in first.Rules)
        {
            rule.RuleSet?.Clear();
            rule.QueryType?.Clear();
            rule.PreferredBy?.Clear();
            rule.IpCidr?.Clear();
            rule.DefaultInterfaceAddress?.Clear();
            foreach (DnsRule child in rule.Rules ?? [])
            {
                child.QueryType?.Clear();
                child.DefaultInterfaceAddress?.Clear();
            }
            rule.Rules?.Clear();
        }
        first.Rules.Clear();
        var changedProfile = profile.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = false } }
        })!.AsObject());
        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Serialize(SingboxDnsEmitter.Emit(plan)), Is.EqualTo(expected));
            Assert.That(plan.AddressFamily, Is.EqualTo(addressFamily));
            Assert.That(plan.SelectionMode, Is.EqualTo(settings.DnsSelectionMode));
            Assert.That(plan.TailscaleEnabled, Is.True);
            Assert.That(DnsProfileBuilder.Plan(profile.Resolve(platform)).TailscaleEnabled, Is.True);
            Assert.That(DnsProfileBuilder.Plan(changedProfile.Resolve(platform)).TailscaleEnabled, Is.False);
            DnsConfig emitted = SingboxDnsEmitter.Emit(plan);
            var evaluations = ResolverBlocks(emitted.Rules).Select(block => emitted.Rules[block.Start]).ToArray();
            var scopes = ExpectedResolverScopes(profile.Resolve(platform)).ToArray();
            string suffix = settings.DnsSelectionMode == DnsResolverSelectionMode.ParallelFastest ? "-a" : "";
            Assert.That(evaluations.Select(rule => rule.Tag),
                Is.EqualTo(scopes.Select(scope => $"response-{scope.Name}-primary{suffix}")));
            Assert.That(evaluations[0].RuleSet, Is.EqualTo(profile.Services.First().RuleSets));
            Assert.That(evaluations[^2].RuleSet, Is.EqualTo(new[] { RuleSetTags.Cn }));
            Assert.That(evaluations[^1].RuleSet, Is.Null);
            Assert.That(plan.Pools.Select(pool => pool.ServiceId),
                Is.EqualTo(profile.Services.OrderByDescending(service => service.PrecedesDomesticRoutes)
                    .Select(service => (string?)service.Id).Concat([null, null])));
            Assert.That(emitted.Rules.Single(rule => rule.Action == DnsRuleAction.Predefined
                && rule.Rcode == DnsResponseCode.NameError).RuleSet, Is.EqualTo(new[] { RuleSetTags.Ads }));
        });
    }

    [Test]
    public void Ipv6AddressRacesPreserveScopeFallbackAndCachePolicyAcrossPlatforms()
    {
        foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
            foreach (AddressFamilyPolicy family in new[] { AddressFamilyPolicy.DualStack, AddressFamilyPolicy.Auto, AddressFamilyPolicy.DirectDualStack })
            {
                var profile = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
                {
                    platforms = new Dictionary<string, object>
                    {
                        [platform.ToString()] = new { dns = new { address_family = family.ToString(), selection_mode = "ParallelFastest" } }
                    }
                })!.AsObject());
                DnsPlan plan = DnsProfileBuilder.Plan(profile.Resolve(platform));
                Assert.DoesNotThrow(plan.Validate);
                DnsConfig dns = SingboxDnsEmitter.Emit(plan);
                foreach (var definition in ExpectedResolverScopes(profile.Resolve(platform)))
                {
                    string primary = $"response-{definition.Name}-primary-aaaa";
                    string secondary = $"response-{definition.Name}-secondary-aaaa";
                    List<DnsRule> pool = GetPoolRules(dns.Rules, primary);
                    Assert.That(pool, Has.Count.EqualTo(9));
                    Assert.That(pool.All(rule => rule.QueryType?.SequenceEqual(["AAAA"]) == true), Is.True);
                    Assert.That(pool.Take(2).Select(rule => (rule.Server, rule.DisableOptimisticCache)),
                        Is.EqualTo(new[] { (definition.PrimaryResolver, (bool?)true), (definition.SecondaryResolver, (bool?)true) }));
                    Assert.That(pool.Take(2).All(rule =>
                        rule.RuleSet?.SequenceEqual(definition.RuleSets ?? []) ?? definition.RuleSets is null), Is.True);
                    Assert.That(pool.Skip(2).Take(2).Select(rule => rule.MatchResponse), Is.EqualTo(new[] { primary, secondary }));
                    Assert.That(pool.Skip(2).Take(2).All(rule => rule.Action == DnsRuleAction.Respond
                        && rule.ResponseRcode == DnsResponseCode.NoError && rule.Race == true
                        && rule.IpCidr?.SequenceEqual(["::/0"]) == true && rule.RuleSet is null), Is.True);
                    AssertResponses(pool.Skip(4).Take(4).ToList(), definition.RuleSets,
                        [primary, secondary, primary, secondary],
                        [DnsResponseCode.NoError, DnsResponseCode.NoError, DnsResponseCode.NameError, DnsResponseCode.NameError],
                        [null, null, null, null]);
                    AssertTerminalServfail(pool[^1], definition.RuleSets);
                    Assert.That(dns.Rules.IndexOf(pool[^1]), Is.LessThan(dns.Rules.FindIndex(rule => rule.Tag == $"response-{definition.Name}-primary")));
                }
            }
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void UserQueryTimeoutOverridePreservesTheSequentialPrimaryBudget(TargetPlatform platform)
    {
        var document = BoxForgeProfile.Default.ApplyOverrides(JsonNode.Parse("""{"dns":{"timeout":"1500ms"}}""")!.AsObject());
        var plan = DnsProfileBuilder.Plan(document.Resolve(platform));
        Assert.DoesNotThrow(plan.Validate);
        var dns = SingboxDnsEmitter.Emit(plan);
        Assert.That(dns.Timeout, Is.EqualTo("1500ms"));
        foreach (var rule in dns.Rules.Where(rule => rule.Action == DnsRuleAction.Evaluate))
            Assert.That(rule.Timeout, Is.EqualTo(platform == TargetPlatform.Android && rule.Tag!.EndsWith("-primary", StringComparison.Ordinal)
                ? "2s" : null));
    }

    [Test]
    public void DnsEmissionExpandsGuardAndServiceResolverPolicy()
    {
        var plan = CreatePlan(TargetPlatform.Linux, tailscaleEnabled: true, AddressFamilyPolicy.Auto);
        DnsConfig dns = SingboxDnsEmitter.Emit(plan);
        var guard = dns.Rules[0];
        var tailscale = dns.Rules[1];
        var blocking = dns.Rules[2];
        var firstService = BoxForgeProfile.Default.Services
            .OrderByDescending(service => service.PrecedesDomesticRoutes).First();
        string resolverTag = $"dns-service-{firstService.Id}-cloudflare";
        var serviceEvaluation = dns.Rules[3];
        Assert.Multiple(() =>
        {
            Assert.That(guard.Mode, Is.EqualTo(RouteLogicalMode.And));
            Assert.That(guard.Rules![0].QueryType, Is.EqualTo(new[] { "AAAA" }));
            Assert.That(guard.Rules![1].DefaultInterfaceAddress,
                Is.EqualTo(new[] { AddressFamilyPolicies.GlobalIpv6Prefix }));
            Assert.That(guard.Rules![1].Invert, Is.True);
            Assert.That(guard.Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(guard.Rcode, Is.EqualTo(DnsResponseCode.NoError));
            Assert.That(tailscale.Server, Is.EqualTo(SingboxTags.TailscaleDns));
            Assert.That(tailscale.DisableOptimisticCache, Is.True);
            Assert.That(blocking.RuleSet, Is.EqualTo(new[] { RuleSetTags.Ads }));
            Assert.That(blocking.Rcode, Is.EqualTo(DnsResponseCode.NameError));
            Assert.That(serviceEvaluation.Action, Is.EqualTo(DnsRuleAction.Evaluate));
            Assert.That(serviceEvaluation.RuleSet, Is.EqualTo(firstService.RuleSets));
            Assert.That(serviceEvaluation.Server, Is.EqualTo(resolverTag));
            Assert.That(serviceEvaluation.QueryType, Is.EqualTo(new[] { "A" }));
            Assert.That(plan.Resolvers.Single(resolver => resolver.Name == resolverTag).Detour,
                Is.EqualTo(firstService.Name));
        });
    }

    [Test]
    public void DnsDiagnosticsCollectPoolDependenciesInStableOrderWithoutDisclosingValues()
    {
        const string hostile = "secret\nforged-path";
        var plan = CreatePlan(TargetPlatform.Linux);
        plan = plan with
        {
            Pools = plan.Pools
                .SetItem(0, plan.Pools[0] with { PrimaryServer = hostile, SecondaryServer = "missing" })
                .SetItem(1, plan.Pools[1] with { PrimaryServer = hostile })
        };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        Assert.Multiple(() =>
        {
            Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[]
            {
                ("IR006", "plan.dns.pools[0].primary_server"),
                ("IR006", "plan.dns.pools[0].secondary_server"),
                ("IR006", "plan.dns.pools[1].primary_server")
            }));
            Assert.That(exception.Message, Does.Not.Contain(hostile));
        });
    }

    [Test]
    public void DefaultDnsPoolsProduceAContractDiagnostic()
    {
        var plan = CreatePlan(TargetPlatform.Linux) with { Pools = default };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)),
            Is.EqualTo(new[] { ("IR008", "plan.dns.pools") }));
    }

    [TestCase("final")]
    [TestCase("primary_resolver")]
    [TestCase("secondary_resolver")]
    [TestCase("same_resolvers")]
    [TestCase("duplicate_resolver")]
    [TestCase("duplicate_service")]
    [TestCase("missing_service_id")]
    [TestCase("early_global")]
    [TestCase("no_global")]
    [TestCase("no_domestic")]
    [TestCase("empty_scope")]
    [TestCase("default_service_scope")]
    [TestCase("global_scope")]
    [TestCase("domestic_scope")]
    [TestCase("unknown_kind")]
    [TestCase("unknown_mode")]
    [TestCase("unknown_family")]
    [TestCase("no_pools")]
    public void InvalidDnsPlanFailsBeforeEmittingRules(string fault)
    {
        var plan = CreatePlan(TargetPlatform.Linux);
        int domestic = plan.Pools.Length - 2;
        int global = plan.Pools.Length - 1;
        plan = fault switch
        {
            "final" => plan with { FinalResolver = "missing" },
            "primary_resolver" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[0] with { PrimaryServer = "missing" }) },
            "secondary_resolver" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[0] with { SecondaryServer = "missing" }) },
            "same_resolvers" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[0] with { SecondaryServer = plan.Pools[0].PrimaryServer }) },
            "duplicate_resolver" => plan with { Resolvers = [.. plan.Resolvers, plan.Resolvers[0]] },
            "duplicate_service" => plan with { Pools = plan.Pools.SetItem(1, plan.Pools[1] with { ServiceId = plan.Pools[0].ServiceId }) },
            "missing_service_id" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[0] with { ServiceId = null }) },
            "early_global" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[global]) },
            "no_global" => plan with { Pools = plan.Pools.RemoveAt(global) },
            "no_domestic" => plan with { Pools = plan.Pools.RemoveAt(domestic) },
            "empty_scope" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[0] with { RuleSets = [] }) },
            "default_service_scope" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[0] with { RuleSets = default }) },
            "global_scope" => plan with { Pools = plan.Pools.SetItem(global, plan.Pools[global] with { RuleSets = [RuleSetTags.Cn] }) },
            "domestic_scope" => plan with { Pools = plan.Pools.SetItem(domestic, plan.Pools[domestic] with { RuleSets = [RuleSetTags.Ads] }) },
            "unknown_kind" => plan with { Pools = plan.Pools.SetItem(0, plan.Pools[0] with { Kind = (DnsPoolKind)999 }) },
            "unknown_family" => plan with { AddressFamily = (AddressFamilyPolicy)999 },
            "no_pools" => plan with { Pools = [] },
            _ => plan with { SelectionMode = (DnsResolverSelectionMode)999 }
        };
        var exception = Assert.Throws<ConfigValidationException>(() => plan.Validate())!;
        var expected = fault switch
        {
            "final" => ("IR006", "plan.dns.final_resolver"),
            "primary_resolver" => ("IR006", "plan.dns.pools[0].primary_server"),
            "secondary_resolver" => ("IR006", "plan.dns.pools[0].secondary_server"),
            "same_resolvers" or "early_global" or "empty_scope" or "default_service_scope" => ("IR011", "plan.dns.pools[0]"),
            "duplicate_resolver" => ("IR009", $"plan.dns.resolvers[{plan.Resolvers.Count - 1}].name"),
            "duplicate_service" => ("IR009", "plan.dns.pools[1].service_id"),
            "missing_service_id" => ("IR009", "plan.dns.pools[0].service_id"),
            "no_global" => ("IR011", $"plan.dns.pools[{global - 1}]"),
            "no_domestic" => ("IR011", $"plan.dns.pools[{domestic - 1}]"),
            "global_scope" => ("IR011", $"plan.dns.pools[{global}]"),
            "domestic_scope" => ("IR011", $"plan.dns.pools[{domestic}]"),
            "unknown_kind" => ("IR008", "plan.dns.pools[0].kind"),
            "unknown_family" => ("IR008", "plan.dns.address_family"),
            "no_pools" => ("IR008", "plan.dns.pools"),
            _ => ("IR008", "plan.dns.selection_mode")
        };
        Assert.That(exception.Diagnostics.Select(item => (item.Code, item.Path)), Does.Contain(expected));
    }

    [TestCase(TargetPlatform.Android, DnsResolverSelectionMode.SequentialFallback)]
    [TestCase(TargetPlatform.Linux, DnsResolverSelectionMode.ParallelFastest)]
    [TestCase(TargetPlatform.Windows, DnsResolverSelectionMode.ParallelFastest)]
    public void ResolverSelectionPolicyMapsPlatforms(
        TargetPlatform platform,
        DnsResolverSelectionMode expected)
    {
        Assert.That(BoxForgeProfile.Default.Resolve(platform).Settings.DnsSelectionMode, Is.EqualTo(expected));
    }

    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.Ipv4Only, DnsStrategy.Ipv4Only, true)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.Ipv4Only, DnsStrategy.Ipv4Only, true)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.Ipv4Only, DnsStrategy.Ipv4Only, true)]
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.DualStack, null, false)]
    [TestCase(TargetPlatform.Android, AddressFamilyPolicy.Auto, null, false)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.DualStack, null, false)]
    [TestCase(TargetPlatform.Linux, AddressFamilyPolicy.Auto, null, false)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.DualStack, null, false)]
    [TestCase(TargetPlatform.Windows, AddressFamilyPolicy.Auto, null, false)]
    public void AddressFamilyPolicyControlsAaaaBlocking(
        TargetPlatform platform,
        AddressFamilyPolicy addressFamily,
        DnsStrategy? strategy,
        bool blocksAaaa)
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            dns = new { address_family = addressFamily.ToString() }
        })!.AsObject());
        DnsConfig dns = SingboxDnsEmitter.Emit(DnsProfileBuilder.Plan(profile.Resolve(platform)));
        int aaaaIndex = dns.Rules.FindIndex(rule =>
            rule.QueryType?.Contains("AAAA") == true
            && rule.Action == DnsRuleAction.Predefined && rule.Rcode == DnsResponseCode.NoError);

        Assert.Multiple(() =>
        {
            Assert.That(dns.Strategy, Is.EqualTo(strategy));
            Assert.That(aaaaIndex >= 0, Is.EqualTo(blocksAaaa));
            if (blocksAaaa)
            {
                int adsIndex = FindRuleSetIndex(dns.Rules, RuleSetTags.Ads);
                int firstPoolIndex = dns.Rules.FindIndex(rule =>
                    rule.Tag == "response-service-ai-primary");
                Assert.That(aaaaIndex, Is.LessThan(adsIndex));
                Assert.That(aaaaIndex, Is.LessThan(firstPoolIndex));
                Assert.That(dns.Rules[aaaaIndex].Rcode, Is.EqualTo(DnsResponseCode.NoError));
            }
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void AutoBlocksAaaaOnlyWhenTheDefaultInterfaceLacksGlobalIpv6(TargetPlatform platform)
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(CreatePlan(platform, addressFamily: AddressFamilyPolicy.Auto));
        DnsRule guard = dns.Rules[0];
        Assert.Multiple(() =>
        {
            Assert.That(dns.Strategy, Is.Null);
            Assert.That(guard.Type, Is.EqualTo(RouteRuleType.Logical));
            Assert.That(guard.Mode, Is.EqualTo(RouteLogicalMode.And));
            Assert.That(guard.Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(guard.Rcode, Is.EqualTo(DnsResponseCode.NoError));
            Assert.That(guard.Rules, Has.Count.EqualTo(2));
            Assert.That(guard.Rules![0].QueryType, Is.EqualTo(new[] { "AAAA" }));
            Assert.That(guard.Rules![1].DefaultInterfaceAddress, Is.EqualTo(new[] { "2000::/3" }));
            Assert.That(guard.Rules![1].Invert, Is.True);
            Assert.That(JsonSerializer.Serialize(guard), Does.Not.Contain("network_interface_address").And.Not.Contain("network_type"));
        });

        // Evaluate the emitted predicate against interface fixtures; this is not a live core/network test.
        var globalPrefix = System.Net.IPNetwork.Parse(guard.Rules![1].DefaultInterfaceAddress!.Single());
        foreach (var fixture in new (string[] Current, string[] Standby, bool Reject)[]
        {
            (["192.0.2.2", "2001:db8::2"], [], false),
            (["192.0.2.2"], [], true),
            (["192.0.2.2", "fe80::2"], [], true),
            (["192.0.2.2", "fd00::2"], [], true),
            ([], [], true),
            (["192.0.2.2"], ["2001:db8::3"], true),
            (["192.0.2.2", "2001:db8::2"], ["192.0.2.3"], false)
        })
        {
            bool addressMatches = fixture.Current.Select(System.Net.IPAddress.Parse).Any(globalPrefix.Contains);
            bool guardMatches = guard.Rules![1].Invert == true ? !addressMatches : addressMatches;
            Assert.That(guardMatches, Is.EqualTo(fixture.Reject),
                $"default=[{string.Join(',', fixture.Current)}], standby=[{string.Join(',', fixture.Standby)}]");
        }
    }

    [Test]
    public void TailscaleRoutePrecedesAdBlockingAndAdRuleReturnsNxDomain()
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(CreatePlan(TargetPlatform.Linux, tailscaleEnabled: true));
        int tailscaleIndex = dns.Rules.FindIndex(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleDns) == true);
        int adsIndex = FindRuleSetIndex(dns.Rules, RuleSetTags.Ads);
        DnsRule adsRule = dns.Rules[adsIndex];

        Assert.Multiple(() =>
        {
            Assert.That(tailscaleIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(tailscaleIndex, Is.LessThan(adsIndex));
            Assert.That(adsRule.Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(adsRule.Rcode, Is.EqualTo(DnsResponseCode.NameError));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void ServiceDnsClassificationMatchesRoutesBeforeDomesticPool(TargetPlatform platform)
    {
        var profile = BoxForgeProfile.Default;
        var plan = CreatePlan(platform);
        DnsConfig dns = SingboxDnsEmitter.Emit(plan);
        RouteConfig route = SingboxRouteEmitter.Emit(RouteProfileBuilder.Plan(profile.Resolve(platform)));
        int adsIndex = FindRuleSetIndex(dns.Rules, RuleSetTags.Ads);
        int domesticIndex = dns.Rules.FindIndex(rule => rule.Tag == DnsResponseTags.DomesticPrimary);

        Assert.Multiple(() =>
        {
            foreach (var service in profile.Services)
            {
                var primary = dns.Rules.Single(rule => rule.Tag == $"response-service-{service.Id}-primary");
                var secondary = dns.Rules.Single(rule => rule.Tag == $"response-service-{service.Id}-secondary");
                Assert.That(primary.Server, Is.EqualTo(SingboxTags.ServiceDns(service.Id, "cloudflare")));
                Assert.That(secondary.Server, Is.EqualTo(SingboxTags.ServiceDns(service.Id, "google")));
                var serviceRoute = route.Rules.Single(rule => rule.Action == RouteRuleAction.Route
                    && rule.RuleSet?.SequenceEqual(service.RuleSets) == true);
                Assert.That(primary.RuleSet, Is.EqualTo(service.RuleSets));
                Assert.That(secondary.RuleSet, Is.EqualTo(service.RuleSets));
                Assert.That(dns.Rules.IndexOf(primary), Is.GreaterThan(adsIndex).And.LessThan(domesticIndex));
                Assert.That(plan.Resolvers.Single(resolver => resolver.Name == primary.Server).Detour,
                    Is.EqualTo(serviceRoute.Outbound).And.EqualTo(service.Name));
                Assert.That(plan.Resolvers.Single(resolver => resolver.Name == secondary.Server).Detour,
                    Is.EqualTo(serviceRoute.Outbound));
            }
        });
    }

    [Test]
    public void OverlappingServicePoolsFollowRoutePhaseAndListOrderWithCustomIdentities()
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonNode.Parse("""
            {"route":{"services":{"replace":[
              {"id":"late","name":"Late service","default_region":"hk","rule_sets":["ai"]},
              {"id":"first","name":"First service","default_region":"jp","rule_sets":["ai"],"precedes_domestic_routes":true},
              {"id":"second","name":"Second service","default_region":"us","rule_sets":["ai"],"precedes_domestic_routes":true}
            ]}}}
            """)!.AsObject());
        var plan = DnsProfileBuilder.Plan(profile.Resolve(TargetPlatform.Linux));
        var dns = SingboxDnsEmitter.Emit(plan);
        var route = SingboxRouteEmitter.Emit(RouteProfileBuilder.Plan(profile.Resolve(TargetPlatform.Linux)));
        var serviceRoutes = route.Rules.Where(rule => rule.RuleSet?.SequenceEqual(["ai"]) == true
            && rule.Action == RouteRuleAction.Route).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ResolverBlocks(dns.Rules).Select(block => dns.Rules[block.Start].Tag),
                Is.EqualTo(new[] { "response-service-first-primary-a", "response-service-second-primary-a",
                    "response-service-late-primary-a", "response-domestic-primary-a", "response-global-primary-a" }));
            Assert.That(serviceRoutes.Select(rule => rule.Outbound),
                Is.EqualTo(new[] { "First service", "Second service", "Late service" }));
            for (int index = 0; index < serviceRoutes.Length; index++)
            {
                string id = new[] { "first", "second", "late" }[index];
                Assert.That(plan.Resolvers.Single(resolver => resolver.Name == SingboxTags.ServiceDns(id, "cloudflare")).Detour,
                    Is.EqualTo(serviceRoutes[index].Outbound));
                Assert.That(dns.Rules.Single(rule => rule.Tag == $"response-service-{id}-primary").RuleSet,
                    Is.EqualTo(new[] { "ai" }));
            }
            Assert.That(plan.Resolvers.Single(resolver => resolver.Name == "dns-service-first-cloudflare").Detour,
                Is.EqualTo("First service"));
        });
    }

    [Test]
    public void ServiceDnsUsesPlatformAndRequestNamesAndResolverOverridesWithoutLeaking()
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonNode.Parse("""
            {"platforms":{"Linux":{"route":{"services":{"patch":[{"id":"ai","name":"Linux AI"}]}}}}}
            """)!.AsObject());
        var request = JsonNode.Parse("""
            {"route":{"services":{"patch":[{"id":"ai","name":"Request AI"}]}},
             "dns":{"resolvers":{"patch":[{"id":"cloudflare","server":"1.0.0.1"}]}}}
            """)!.AsObject();
        var effective = profile.Resolve(TargetPlatform.Linux, request);
        var plan = DnsProfileBuilder.Plan(effective);
        var resolver = plan.Resolvers.Single(resolver => resolver.Name == "dns-service-ai-cloudflare");
        var route = SingboxRouteEmitter.Emit(RouteProfileBuilder.Plan(effective));

        Assert.Multiple(() =>
        {
            Assert.That(resolver.Detour, Is.EqualTo("Request AI"));
            Assert.That(resolver.Address, Is.EqualTo("1.0.0.1"));
            Assert.That(route.Rules.Single(rule => rule.Action == RouteRuleAction.Route
                && rule.RuleSet?.SequenceEqual(["ai"]) == true).Outbound, Is.EqualTo(resolver.Detour));
            Assert.That(DnsProfileBuilder.Plan(profile.Resolve(TargetPlatform.Linux)).Resolvers.Single(resolver => resolver.Name == "dns-service-ai-cloudflare").Detour,
                Is.EqualTo("Linux AI"));
            Assert.That(DnsProfileBuilder.Plan(profile.Resolve(TargetPlatform.Windows)).Resolvers.Single(resolver => resolver.Name == "dns-service-ai-cloudflare").Detour,
                Is.EqualTo(BoxForgeProfile.Default.Services.Single(service => service.Id == "ai").Name));
        });
    }

    [Test]
    public void AndroidPoolsQuerySecondaryOnlyAfterPrimaryIsUnacceptable()
    {
        var plan = CreatePlan(TargetPlatform.Android);
        DnsConfig dns = SingboxDnsEmitter.Emit(plan);
        foreach (var pool in ExpectedResolverScopes(BoxForgeProfile.Default.Resolve(TargetPlatform.Android)))
        {
            AssertSequentialFallbackPool(dns.Rules,
                $"response-{pool.Name}-primary", $"response-{pool.Name}-secondary",
                pool.RuleSets, pool.PrimaryResolver, pool.SecondaryResolver);
        }
    }

    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void DesktopPoolsRaceOnlyNoErrorAndPreferPrimaryNxDomain(
        TargetPlatform platform)
    {
        var plan = CreatePlan(platform);
        DnsConfig dns = SingboxDnsEmitter.Emit(plan);
        foreach (var pool in ExpectedResolverScopes(BoxForgeProfile.Default.Resolve(platform)))
        {
            AssertParallelFastestPool(dns.Rules,
                $"response-{pool.Name}-primary", $"response-{pool.Name}-secondary",
                pool.RuleSets, pool.PrimaryResolver, pool.SecondaryResolver);
        }
    }

    [Test]
    public void DesktopServiceRuleShapeEncodesPositiveRaceAndOrderedNxDomain()
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(CreatePlan(TargetPlatform.Linux));
        List<DnsRule> pool = GetPoolRules(
            dns.Rules,
            "response-service-ai-primary");

        List<DnsRule> bothPositive = MatchingResponses(
            pool, DnsResponseCode.NoError, DnsResponseCode.NoError);
        List<DnsRule> primaryNxSecondaryPositive = MatchingResponses(
            pool, DnsResponseCode.NameError, DnsResponseCode.NoError);
        List<DnsRule> bothNx = MatchingResponses(
            pool, DnsResponseCode.NameError, DnsResponseCode.NameError);
        List<DnsRule> primaryFailureSecondaryPositive = MatchingResponses(
            pool, null, DnsResponseCode.NoError);
        List<DnsRule> bothFailure = MatchingResponses(pool, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(bothPositive.Select(rule => rule.MatchResponse),
                Is.EqualTo(new[]
                {
                    "response-service-ai-primary",
                    "response-service-ai-secondary"
                }));
            Assert.That(bothPositive.All(rule => rule.Race == true), Is.True);
            Assert.That(primaryNxSecondaryPositive.Select(rule =>
                    (rule.MatchResponse, rule.ResponseRcode, rule.Race)),
                Is.EqualTo(new[]
                {
                    ("response-service-ai-secondary",
                        (DnsResponseCode?)DnsResponseCode.NoError, (bool?)true),
                    ("response-service-ai-primary",
                        (DnsResponseCode?)DnsResponseCode.NameError, (bool?)null)
                }));
            Assert.That(bothNx.Select(rule => rule.MatchResponse),
                Is.EqualTo(new[]
                {
                    "response-service-ai-primary",
                    "response-service-ai-secondary"
                }));
            Assert.That(bothNx.All(rule => rule.Race is null), Is.True);
            Assert.That(primaryFailureSecondaryPositive.Single().MatchResponse,
                Is.EqualTo("response-service-ai-secondary"));
            Assert.That(primaryFailureSecondaryPositive.Single().Race, Is.True);
            Assert.That(bothFailure, Is.Empty);
            Assert.That(pool[^1].Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(pool[^1].Rcode, Is.EqualTo(DnsResponseCode.ServerFailure));
        });
    }

    [TestCase(TargetPlatform.Linux, "response-service-ai-primary", "response-service-ai-secondary")]
    [TestCase(TargetPlatform.Linux, DnsResponseTags.DomesticPrimary, DnsResponseTags.DomesticSecondary)]
    [TestCase(TargetPlatform.Linux, DnsResponseTags.GlobalPrimary, DnsResponseTags.GlobalSecondary)]
    [TestCase(TargetPlatform.Windows, "response-service-ai-primary", "response-service-ai-secondary")]
    [TestCase(TargetPlatform.Windows, DnsResponseTags.DomesticPrimary, DnsResponseTags.DomesticSecondary)]
    [TestCase(TargetPlatform.Windows, DnsResponseTags.GlobalPrimary, DnsResponseTags.GlobalSecondary)]
    public void DesktopAddressPoolsPreferIpv4BeforeEmptyAnswersWithoutUsingExpiredCache(
        TargetPlatform platform, string primaryTag, string secondaryTag)
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(CreatePlan(platform));
        List<DnsRule> pool = GetPoolRules(dns.Rules, primaryTag + "-a");
        DnsRule generalPrimary = dns.Rules.Single(rule => rule.Tag == primaryTag);

        Assert.Multiple(() =>
        {
            Assert.That(pool, Has.Count.EqualTo(9));
            Assert.That(pool.All(rule => rule.QueryType?.SequenceEqual(["A"]) == true), Is.True);
            Assert.That(pool.Take(2).All(rule =>
                rule.Action == DnsRuleAction.Evaluate && rule.DisableOptimisticCache == true), Is.True);
            Assert.That(pool[0].Server, Is.EqualTo(generalPrimary.Server));
            Assert.That(pool[1].Tag, Is.EqualTo(secondaryTag + "-a"));
            Assert.That(pool.Skip(2).Take(2).All(rule =>
                rule.Action == DnsRuleAction.Respond
                && rule.ResponseRcode == DnsResponseCode.NoError
                && rule.Race == true
                && rule.IpCidr?.SequenceEqual(["0.0.0.0/0"]) == true
                && rule.RuleSet is null), Is.True,
                "Address races inherit scope from unique response tags, not OR-combined domain matchers.");
            AssertResponses(pool.Skip(4).Take(4).ToList(), generalPrimary.RuleSet,
                [primaryTag + "-a", secondaryTag + "-a", primaryTag + "-a", secondaryTag + "-a"],
                [DnsResponseCode.NoError, DnsResponseCode.NoError,
                    DnsResponseCode.NameError, DnsResponseCode.NameError],
                [null, null, null, null]);
            AssertTerminalServfail(pool[^1], generalPrimary.RuleSet);
            Assert.That(dns.Rules.IndexOf(pool[^1]), Is.LessThan(dns.Rules.IndexOf(generalPrimary)));
            Assert.That(generalPrimary.DisableOptimisticCache, Is.Null);
            Assert.That(dns.Timeout, Is.EqualTo("3s"));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void DefaultIpv4OnlyPolicyControlsDnsAndRouteIpv6Guards(TargetPlatform platform)
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(DnsProfileBuilder.Plan(BoxForgeProfile.Default.Resolve(platform)));
        RouteConfig route = SingboxRouteEmitter.Emit(RouteProfileBuilder.Plan(BoxForgeProfile.Default.Resolve(platform)));

        Assert.That(BoxForgeProfile.Default.Resolve(platform).Settings.AddressFamily,
            Is.EqualTo(AddressFamilyPolicy.Ipv4Only));
        Assert.That(dns.Strategy, Is.EqualTo(DnsStrategy.Ipv4Only));
        Assert.That(dns.Rules[0].QueryType, Is.EqualTo(new[] { "AAAA" }));
        Assert.That(dns.Rules[0].Rules, Is.Null);
        Assert.That(dns.Rules[0].Rcode, Is.EqualTo(DnsResponseCode.NoError));
        Assert.That(route.Rules.Count(rule =>
            rule.Action == RouteRuleAction.Reject && rule.IpVersion == 6 && rule.RuleSet is null),
            Is.EqualTo(2));
    }

    [TestCase(TargetPlatform.Android, DnsResolverSelectionMode.SequentialFallback)]
    [TestCase(TargetPlatform.Linux, DnsResolverSelectionMode.SequentialFallback)]
    [TestCase(TargetPlatform.Windows, DnsResolverSelectionMode.SequentialFallback)]
    [TestCase(TargetPlatform.Android, DnsResolverSelectionMode.ParallelFastest)]
    [TestCase(TargetPlatform.Linux, DnsResolverSelectionMode.ParallelFastest)]
    [TestCase(TargetPlatform.Windows, DnsResolverSelectionMode.ParallelFastest)]
    public void DirectDualStackPreservesAaaaAcrossAllPoolsRegardlessOfSelectorDetour(
        TargetPlatform platform, DnsResolverSelectionMode selectionMode)
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            platforms = new Dictionary<string, object>
            {
                [platform.ToString()] = new { dns = new { address_family = "DirectDualStack", selection_mode = selectionMode.ToString() } }
            }
        })!.AsObject());
        DnsPlan plan = DnsProfileBuilder.Plan(profile.Resolve(platform));
        DnsConfig dns = SingboxDnsEmitter.Emit(plan);
        Assert.DoesNotThrow(plan.Validate);
        Assert.That(plan.AddressFamily, Is.EqualTo(AddressFamilyPolicy.DirectDualStack));
        Assert.That(plan.SelectionMode, Is.EqualTo(selectionMode));
        Assert.That(dns.Strategy, Is.Null);
        Assert.That(dns.Rules[0].Rules![0].QueryType, Is.EqualTo(new[] { "AAAA" }));
        Assert.That(dns.Rules[0].Rules![1].DefaultInterfaceAddress, Is.EqualTo(new[] { "2000::/3" }));
        Assert.That(dns.Rules[0].Rules![1].Invert, Is.True);
        Assert.That(dns.Rules.Skip(1).Any(rule =>
            rule.Action == DnsRuleAction.Predefined && rule.Rcode == DnsResponseCode.NoError), Is.False,
            "DNS classification cannot determine whether a selector or cnip route ultimately chooses DIRECT.");
        var blocks = ResolverBlocks(dns.Rules).ToArray();
        Assert.That(blocks, Has.Length.EqualTo(profile.Resolve(platform).Services.Length + 2));
        Assert.That(dns.Rules[blocks[^1].Start].RuleSet, Is.Null);
        foreach (var pool in ExpectedResolverScopes(profile.Resolve(platform)))
        {
            DnsRule evaluation = dns.Rules.Single(rule => rule.Tag == $"response-{pool.Name}-primary");
            Assert.That(evaluation.Action, Is.EqualTo(DnsRuleAction.Evaluate), pool.Name);
            Assert.That(evaluation.QueryType, Is.Null, pool.Name);
            Assert.That(evaluation.RuleSet, Is.EqualTo(pool.RuleSets), pool.Name);
            Assert.That(evaluation.Server, Is.EqualTo(pool.PrimaryResolver), pool.Name);
        }
    }

    [Test]
    public void PoolsEndWithScopedServfailAndDoNotFallThroughToAnotherPool()
    {
        var plan = CreatePlan(TargetPlatform.Linux);
        DnsConfig dns = SingboxDnsEmitter.Emit(plan);
        var blocks = ResolverBlocks(dns.Rules).ToArray();
        var scopes = ExpectedResolverScopes(BoxForgeProfile.Default.Resolve(TargetPlatform.Linux)).ToArray();
        Assert.That(blocks, Has.Length.EqualTo(scopes.Length));
        for (int index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            var scope = scopes[index];
            string primaryTag = $"response-{scope.Name}-primary";
            string secondaryTag = $"response-{scope.Name}-secondary";
            int primaryIndex = dns.Rules.FindIndex(rule => rule.Tag == primaryTag);
            int terminalIndex = FindTerminalServfailIndex(dns.Rules, primaryIndex, scope.RuleSets);
            int lastResponseIndex = dns.Rules.FindLastIndex(rule =>
                rule.MatchResponse == primaryTag || rule.MatchResponse == secondaryTag);

            Assert.That(terminalIndex, Is.EqualTo(block.End).And.GreaterThan(lastResponseIndex).And.GreaterThan(primaryIndex), primaryTag);
            AssertTerminalServfail(dns.Rules[block.End], scope.RuleSets);
            if (index + 1 < blocks.Length)
                Assert.That(block.End + 1, Is.EqualTo(blocks[index + 1].Start), primaryTag);
        }
    }

    [Test]
    public void ResolverServersHaveStableTransportIdentityAndCachePolicy()
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(CreatePlan(TargetPlatform.Linux));
        Dictionary<string, HttpsDnsServer> servers = dns.Servers
            .OfType<HttpsDnsServer>()
            .ToDictionary(server => server.Tag!, StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(servers.Keys, Does.Contain(SingboxTags.DirectAliDns));
            Assert.That(servers.Keys, Does.Contain(SingboxTags.DirectTencentDns));
            Assert.That(servers.Keys, Does.Contain(SingboxTags.ProxyCloudflareDns));
            Assert.That(servers.Keys, Does.Contain(SingboxTags.ProxyGoogleDns));
            Assert.That(JsonSerializer.SerializeToElement<DnsServer>(servers[SingboxTags.DirectAliDns]).GetProperty("type").GetString(), Is.EqualTo("https"));
            Assert.That(servers[SingboxTags.DirectAliDns].Server, Is.EqualTo("223.5.5.5"));
            Assert.That(servers[SingboxTags.DirectAliDns].Tls?.ServerName, Is.EqualTo("dns.alidns.com"));
            Assert.That(servers[SingboxTags.DirectTencentDns].Server, Is.EqualTo("119.29.29.29"));
            Assert.That(servers[SingboxTags.DirectTencentDns].Tls?.ServerName, Is.EqualTo("doh.pub"));
            Assert.That(servers[SingboxTags.ProxyCloudflareDns].Server, Is.EqualTo("1.1.1.1"));
            Assert.That(servers[SingboxTags.ProxyCloudflareDns].Tls?.ServerName, Is.EqualTo("cloudflare-dns.com"));
            Assert.That(servers[SingboxTags.ProxyGoogleDns].Server, Is.EqualTo("8.8.8.8"));
            Assert.That(servers[SingboxTags.ProxyGoogleDns].Tls?.ServerName, Is.EqualTo("dns.google"));
            Assert.That(servers[SingboxTags.DirectAliDns].Detour, Is.Null);
            Assert.That(servers[SingboxTags.DirectTencentDns].Detour, Is.Null);
            Assert.That(servers[SingboxTags.ProxyCloudflareDns].Detour, Is.EqualTo(SingboxTags.MainProxyGroup));
            Assert.That(servers[SingboxTags.ProxyGoogleDns].Detour, Is.EqualTo(SingboxTags.MainProxyGroup));
            Assert.That(dns.CacheCapacity, Is.EqualTo(4096));
            Assert.That(dns.Final, Is.EqualTo(SingboxTags.ProxyCloudflareDns));
            Assert.That(dns.Optimistic!.Enabled, Is.True);
            Assert.That(dns.Optimistic!.Timeout, Is.EqualTo("6h"));
            Assert.That(dns.ReverseMapping, Is.True);
        });
    }

    [Test]
    public void BuilderDoesNotEmitNodeDomainSpecificDnsRules()
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(CreatePlan(TargetPlatform.Linux));

        Assert.That(dns.Rules.Any(rule => rule.Domain is not null), Is.False);
    }

    [Test]
    public void TailscaleResolverDisablesOptimisticCache()
    {
        DnsConfig dns = SingboxDnsEmitter.Emit(CreatePlan(TargetPlatform.Linux, tailscaleEnabled: true));
        DnsRule tailscaleRule = dns.Rules.Single(rule =>
            rule.PreferredBy?.Contains(SingboxTags.TailscaleDns) == true);

        Assert.That(tailscaleRule.DisableOptimisticCache, Is.True);
    }

    private static void AssertSequentialFallbackPool(
        List<DnsRule> rules,
        string primaryTag,
        string secondaryTag,
        IReadOnlyList<string>? expectedRuleSets,
        string expectedPrimaryServer,
        string expectedSecondaryServer)
    {
        int primaryIndex = rules.FindIndex(rule => rule.Tag == primaryTag);
        int secondaryIndex = rules.FindIndex(rule => rule.Tag == secondaryTag);
        Assert.That(primaryIndex, Is.GreaterThanOrEqualTo(0), primaryTag);
        Assert.That(secondaryIndex, Is.EqualTo(primaryIndex + 3), secondaryTag);

        DnsRule primary = rules[primaryIndex];
        DnsRule secondary = rules[secondaryIndex];
        List<DnsRule> primaryResponses = rules
            .Skip(primaryIndex + 1)
            .Take(2)
            .ToList();
        List<DnsRule> secondaryResponses = rules
            .Skip(secondaryIndex + 1)
            .Take(2)
            .ToList();
        int terminalIndex = secondaryIndex + 3;

        Assert.Multiple(() =>
        {
            AssertEvaluate(primary, expectedRuleSets, expectedPrimaryServer,
                primaryTag, "2s");
            AssertEvaluate(secondary, expectedRuleSets, expectedSecondaryServer,
                secondaryTag, null);
            AssertResponses(primaryResponses, expectedRuleSets,
                [primaryTag, primaryTag],
                [DnsResponseCode.NoError, DnsResponseCode.NameError],
                [null, null]);
            AssertResponses(secondaryResponses, expectedRuleSets,
                [secondaryTag, secondaryTag],
                [DnsResponseCode.NoError, DnsResponseCode.NameError],
                [null, null]);
            AssertTerminalServfail(rules[terminalIndex], expectedRuleSets);
        });
    }

    private static IEnumerable<(int Start, int End)> ResolverBlocks(List<DnsRule> rules)
    {
        int start = -1;
        for (int index = 0; index < rules.Count; index++)
        {
            DnsRule rule = rules[index];
            if (start < 0 && rule.Action == DnsRuleAction.Evaluate) start = index;
            if (start >= 0 && rule.Action == DnsRuleAction.Predefined
                && rule.Rcode == DnsResponseCode.ServerFailure && rule.QueryType is null)
            {
                yield return (start, index);
                start = -1;
            }
        }
    }

    private static List<DnsRule> GetPoolRules(
        List<DnsRule> rules,
        string primaryTag)
    {
        int start = rules.FindIndex(rule => rule.Tag == primaryTag);
        int end = rules.FindIndex(start, rule =>
            rule.Action == DnsRuleAction.Predefined
            && rule.Rcode == DnsResponseCode.ServerFailure);
        return rules.Skip(start).Take(end - start + 1).ToList();
    }

    private static List<DnsRule> MatchingResponses(
        List<DnsRule> pool,
        DnsResponseCode? primary,
        DnsResponseCode? secondary) =>
        pool.Where(rule => rule.Action == DnsRuleAction.Respond
                && rule.ResponseRcode == (rule.MatchResponse ==
                    "response-service-ai-primary" ? primary : secondary))
            .ToList();

    private static void AssertParallelFastestPool(
        List<DnsRule> rules,
        string primaryTag,
        string secondaryTag,
        IReadOnlyList<string>? expectedRuleSets,
        string expectedPrimaryServer,
        string expectedSecondaryServer)
    {
        int primaryIndex = rules.FindIndex(rule => rule.Tag == primaryTag);
        int secondaryIndex = rules.FindIndex(rule => rule.Tag == secondaryTag);
        Assert.That(primaryIndex, Is.GreaterThanOrEqualTo(0), primaryTag);
        Assert.That(secondaryIndex, Is.EqualTo(primaryIndex + 1), secondaryTag);

        DnsRule primary = rules[primaryIndex];
        DnsRule secondary = rules[secondaryIndex];
        List<DnsRule> responses = rules.Skip(secondaryIndex + 1).Take(4).ToList();
        int terminalIndex = secondaryIndex + 5;
        string[] responseOrder =
        [
            primaryTag,
            secondaryTag,
            primaryTag,
            secondaryTag
        ];
        DnsResponseCode[] responseCodes =
        [
            DnsResponseCode.NoError,
            DnsResponseCode.NoError,
            DnsResponseCode.NameError,
            DnsResponseCode.NameError
        ];

        Assert.Multiple(() =>
        {
            AssertEvaluate(primary, expectedRuleSets, expectedPrimaryServer,
                primaryTag, null);
            AssertEvaluate(secondary, expectedRuleSets, expectedSecondaryServer,
                secondaryTag, null);
            AssertResponses(responses, expectedRuleSets, responseOrder, responseCodes,
                [true, true, null, null]);
            AssertTerminalServfail(rules[terminalIndex], expectedRuleSets);
        });
    }

    private static void AssertEvaluate(
        DnsRule rule,
        IReadOnlyList<string>? expectedRuleSets,
        string expectedServer,
        string expectedTag,
        string? expectedTimeout)
    {
        Assert.Multiple(() =>
        {
            Assert.That(rule.Action, Is.EqualTo(DnsRuleAction.Evaluate));
            Assert.That(rule.Server, Is.EqualTo(expectedServer));
            Assert.That(rule.Tag, Is.EqualTo(expectedTag));
            Assert.That(rule.RuleSet, Is.EqualTo(expectedRuleSets));
            Assert.That(rule.Timeout, Is.EqualTo(expectedTimeout));
        });
    }

    private static void AssertResponses(
        List<DnsRule> responses,
        IReadOnlyList<string>? expectedRuleSets,
        string[] expectedTags,
        DnsResponseCode[] expectedCodes,
        bool?[] expectedRace)
    {
        Assert.Multiple(() =>
        {
            Assert.That(responses.Select(rule => rule.MatchResponse),
                Is.EqualTo(expectedTags));
            Assert.That(responses.Select(rule => rule.ResponseRcode),
                Is.EqualTo(expectedCodes));
            Assert.That(responses.Select(rule => rule.Race), Is.EqualTo(expectedRace));
            Assert.That(responses.All(rule => rule.Action == DnsRuleAction.Respond),
                Is.True);
            Assert.That(responses.All(rule =>
                    rule.RuleSet?.SequenceEqual(expectedRuleSets ?? [])
                    ?? expectedRuleSets is null),
                Is.True);
        });
    }

    private static void AssertTerminalServfail(
        DnsRule rule,
        IReadOnlyList<string>? expectedRuleSets)
    {
        Assert.Multiple(() =>
        {
            Assert.That(rule.Action, Is.EqualTo(DnsRuleAction.Predefined));
            Assert.That(rule.Rcode, Is.EqualTo(DnsResponseCode.ServerFailure));
            Assert.That(rule.RuleSet, Is.EqualTo(expectedRuleSets));
        });
    }

    private static int FindTerminalServfailIndex(
        List<DnsRule> rules,
        int poolStartIndex,
        IReadOnlyList<string>? ruleSets) => rules.FindIndex(poolStartIndex, rule =>
        rule.Action == DnsRuleAction.Predefined
        && rule.Rcode == DnsResponseCode.ServerFailure
        && ((rule.RuleSet is null && ruleSets is null)
            || (rule.RuleSet is not null && ruleSets is not null
                && rule.RuleSet.SequenceEqual(ruleSets))));

    private static int FindRuleSetIndex(List<DnsRule> rules, string ruleSet) =>
        rules.FindIndex(rule => rule.RuleSet?.Contains(ruleSet) == true);

    private static IEnumerable<(string Name, IReadOnlyList<string>? RuleSets, string PrimaryResolver, string SecondaryResolver)>
        ExpectedResolverScopes(ResolvedProfile profile)
    {
        foreach (var service in profile.Services.OrderByDescending(service => service.PrecedesDomesticRoutes))
            yield return ($"service-{service.Id}", service.RuleSets,
                SingboxTags.ServiceDns(service.Id, "cloudflare"), SingboxTags.ServiceDns(service.Id, "google"));
        yield return ("domestic", new[] { RuleSetTags.Cn }, SingboxTags.DirectAliDns, SingboxTags.DirectTencentDns);
        yield return ("global", null, SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns);
    }

    private static DnsPlan CreatePlan(
        TargetPlatform platform, bool tailscaleEnabled = false, AddressFamilyPolicy? addressFamily = null) =>
        DnsProfileBuilder.Plan(BoxForgeProfile.Default.ApplyOverrides(JsonSerializer.SerializeToNode(new
        {
            features = new { tailscale = new { enabled = tailscaleEnabled } },
            dns = addressFamily is null ? null : new { address_family = addressFamily.ToString() }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!.AsObject()).Resolve(platform));
}
