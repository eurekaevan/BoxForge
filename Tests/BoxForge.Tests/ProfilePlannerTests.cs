using BoxForge.Models.Intermediate;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Parsers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProfilePlannerTests
{
    [Test]
    public void EmptySniffExclusionsKeepAnUnrestrictedTcpSniffCondition()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"version":1,"policies":{"sniff_excluded_ports":[]}}
            """));
        var profile = BoxForgeProfile.Load(source);
        var route = SingboxRouteEmitter.Emit(new RouteProfileBuilder(profile).Plan(TargetPlatform.Linux));
        var tcpSniff = route.Rules.Single(rule => rule.Action == RouteRuleAction.Sniff && rule.Rules is not null);
        Assert.Multiple(() =>
        {
            Assert.That(tcpSniff.Rules, Has.Count.EqualTo(1));
            Assert.That(tcpSniff.Rules![0].Inbound, Is.EqualTo(new[] { SingboxTags.TunInbound }));
            Assert.That(tcpSniff.Rules[0].Network, Is.EqualTo(new[] { "tcp" }));
            Assert.That(tcpSniff.Rules[0].Invert, Is.Null);
            Assert.That(tcpSniff.Rules[0].Port, Is.Null);
        });
    }

    [Test]
    public void ProfilePoliciesReachGroupsDnsRouteAndPlatformOutputs()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"version":1,
             "policies":{"main_default_region":"jp","sniff_timeout":"450ms","sniff_excluded_ports":[25,465],
                         "url_test_url":"https://probe.example.com/check","url_test_tolerance":80,"url_test_idle_timeout":"45m",
                         "dns_timeout":"7s","dns_cache_capacity":8192,"dns_optimistic_timeout":"3h"},
             "platforms":{"Linux":{"connect_timeout":"9s","tun_addresses":["172.20.0.1/30","fd01::1/126"]}}}
            """));
        var profile = BoxForgeProfile.Load(source);
        var nodes = CreateCatalog("美国 01", "美国 02", "日本 01", "日本 02");
        var parsedNodes = new NodeCatalogBuilder(profile)
            .Build(new ClashParser().Parse(ProfileConfigurationTests.Yaml("parsed"))!);
        var settings = profile.GetPlatform(TargetPlatform.Linux);
        var groups = ProfilePlanner.PlanGroups(nodes, profile, settings.UrlTestInterval);
        var dns = SingboxDnsEmitter.Emit(new DnsProfileBuilder(profile).Plan(TargetPlatform.Linux));
        var route = SingboxRouteEmitter.Emit(new RouteProfileBuilder(profile).Plan(TargetPlatform.Linux));
        var tcpSniff = route.Rules.Single(rule => rule.Action == RouteRuleAction.Sniff && rule.Rules is not null);
        Assert.Multiple(() =>
        {
            Assert.That(groups.Main.Preferred, Is.EqualTo(RegionName("jp")));
            Assert.That(groups.Probes.Select(probe => probe.TestUrl), Is.All.EqualTo("https://probe.example.com/check"));
            Assert.That(groups.Probes.Select(probe => probe.Tolerance), Is.All.EqualTo(80));
            Assert.That(groups.Probes.Select(probe => probe.IdleTimeout), Is.All.EqualTo("45m"));
            Assert.That(dns.Timeout, Is.EqualTo("7s"));
            Assert.That(dns.CacheCapacity, Is.EqualTo(8192));
            Assert.That(dns.Optimistic.Timeout, Is.EqualTo("3h"));
            Assert.That(route.Rules.Where(rule => rule.Action == RouteRuleAction.Sniff).Select(rule => rule.Timeout),
                Is.All.EqualTo("450ms"));
            Assert.That(tcpSniff.Rules!.Single(rule => rule.Invert == true).Port, Is.EqualTo(new[] { 25, 465 }));
            Assert.That(InboundBuilder.Emit(new TunPlan(settings.TunAddresses, settings.AutoRedirect, settings.MultiQueue, settings.StrictRoute)).Single().Address,
                Is.EqualTo(new[] { "172.20.0.1/30", "fd01::1/126" }));
            Assert.That(nodes.Nodes.Select(node => SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(node, settings)).ConnectTimeout), Is.All.EqualTo("9s"));
            Assert.That(SingboxNodeEmitter.Emit(OutboundTuningPolicy.Apply(parsedNodes.Nodes.Single(), settings)).ConnectTimeout, Is.EqualTo("9s"));
        });
    }

    [Test]
    public void GroupIrKeepsPlatformPolicyAndEmitsIndependentCollections()
    {
        string[] names = ["美国 01", "美国 02", "香港 01"];
        var nodes = new NodeCatalog([], names);
        var platform = BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux);
        var plan = ProfilePlanner.PlanGroups(nodes, BoxForgeProfile.Default, platform.UrlTestInterval);
        var first = SingboxGroupEmitter.Emit(plan);
        first.MainOutbound.Outbounds.Clear();
        first.RegionOutbounds.Single().Outbounds.Clear();
        first.RegionAutoOutbounds.Single().Outbounds.Clear();
        first.ServiceOutbounds[0].Outbounds.Clear();
        names[0] = "changed";
        var second = SingboxGroupEmitter.Emit(plan);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Leaves, Is.EqualTo(new[] { "美国 01", "美国 02", "香港 01" }));
            Assert.That(second.MainOutbound.Outbounds, Does.Contain("美国 01"));
            Assert.That(second.RegionOutbounds.Single().Outbounds, Has.Count.EqualTo(3));
            Assert.That(second.RegionAutoOutbounds.Single().Outbounds, Is.EqualTo(new[] { "美国 01", "美国 02" }));
            Assert.That(second.RegionAutoOutbounds.Single().Interval, Is.EqualTo(platform.UrlTestInterval));
            Assert.That(second.ServiceOutbounds[0].Outbounds, Is.Not.Empty);
            Assert.That(plan.Probes.Single().Candidates, Is.EqualTo(new[] { "美国 01", "美国 02" }));
        });
    }

    [TestCase("default")]
    [TestCase("target")]
    [TestCase("probe_group")]
    [TestCase("probe_single_leaf")]
    public void InvalidGroupIrFailsBeforeTargetEmission(string fault)
    {
        var plan = ProfilePlanner.PlanGroups(CreateCatalog("美国 01", "美国 02"), BoxForgeProfile.Default, "3m");
        plan = fault switch
        {
            "default" => plan with { Main = plan.Main with { Preferred = "missing" } },
            "target" => plan with { Main = plan.Main with { Candidates = [.. plan.Main.Candidates, "missing"] } },
            "probe_group" => plan with { Probes = [plan.Probes[0] with { Candidates = [plan.Main.Name, "美国 01"] }] },
            _ => plan with { Probes = [plan.Probes[0] with { Candidates = ["美国 01"] }] }
        };
        Assert.Throws<InvalidOperationException>(() => SingboxGroupEmitter.Emit(plan));
    }

    [TestCase("", "SINGAPORE", true)]
    [TestCase("tr-TR", "SINGAPORE", true)]
    [TestCase("tr-TR", "sİngapore", false)]
    [TestCase("tr-TR", "sıngapore", false)]
    public void RegionPatternsPreserveGeneratedRegexCultureIndependence(string culture, string name, bool matches)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(ReadDefaultProfile()));
            var profile = BuiltInProfile.Load(source);
            var definition = profile.Regions.Single(region => region.Id == "sg");
            // Recreate the production pattern under this culture, regardless of static initialization order.
            var pattern = new System.Text.RegularExpressions.Regex(definition.Pattern,
                BoxForgeProfile.Default.Regions.Single(region => region.Id == "sg").Pattern.Options);
            Assert.That(pattern.IsMatch(name), Is.EqualTo(matches));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCase("root")]
    [TestCase("region")]
    [TestCase("service")]
    [TestCase("platform")]
    [TestCase("rule_set")]
    [TestCase("resolver")]
    public void BuiltInProfileRejectsUnknownKeysAtEveryLevel(string level)
    {
        var root = JsonNode.Parse(ReadDefaultProfile())!;
        JsonNode target = level switch
        {
            "region" => root["regions"]![0]!,
            "service" => root["services"]![0]!,
            "platform" => root["platforms"]!["Android"]!,
            "rule_set" => root["rule_sets"]![0]!,
            "resolver" => root["resolvers"]![0]!,
            _ => root
        };
        target["typo"] = true;
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.Throws<JsonException>(() => BuiltInProfile.Load(source));
    }

    [TestCase("duplicate")]
    [TestCase("version")]
    [TestCase("missing_platform")]
    [TestCase("missing_platform_field")]
    [TestCase("unknown_enum")]
    [TestCase("numeric_enum")]
    [TestCase("null")]
    public void BuiltInProfileRejectsMalformedData(string fault)
    {
        var root = JsonNode.Parse(ReadDefaultProfile())!;
        switch (fault)
        {
            case "version": root["version"] = 2; break;
            case "missing_platform": root["platforms"]!.AsObject().Remove("Android"); break;
            case "missing_platform_field": root["platforms"]!["Android"]!.AsObject().Remove("dns_selection_mode"); break;
            case "unknown_enum": root["platforms"]!["Android"]!["dns_selection_mode"] = "Unknown"; break;
            case "numeric_enum": root["platforms"]!["Android"]!["dns_selection_mode"] = 123; break;
        }
        string json = fault switch
        {
            "duplicate" => ReadDefaultProfile().Replace("\"version\": 1", "\"version\": 1, \"version\": 1", StringComparison.Ordinal),
            "null" => "null",
            _ => root.ToJsonString()
        };
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<JsonException>(() => BuiltInProfile.Load(source));
    }

    private static string ReadDefaultProfile()
    {
        using Stream source = typeof(BoxForgeProfile).Assembly.GetManifestResourceStream(
            "BoxForge.Configuration.default-profile.json")!;
        using var reader = new StreamReader(source);
        return reader.ReadToEnd();
    }

    [Test]
    public void MultipleNodesCreateLeafOnlyRegionalAutoGroups()
    {
        NodeCatalog nodes = CreateCatalog(
            "美国 01",
            "美国 02",
            "日本 01",
            "日本 02",
            "香港 01",
            "香港 02",
            "新加坡 01",
            "新加坡 02",
            "其他 01");

        ProfilePlan plan = Plan(nodes, BoxForgeProfile.Default);
        HashSet<string> leafTags = nodes.Nodes
            .Select(node => node.Name)
            .ToHashSet(StringComparer.Ordinal);
        string unitedStates = RegionName("us");

        Assert.Multiple(() =>
        {
            Assert.That(plan.RegionOutbounds, Has.Count.EqualTo(4));
            Assert.That(plan.RegionAutoOutbounds, Has.Count.EqualTo(4));
            Assert.That(
                plan.RegionOutbounds.Select(outbound => outbound.Tag),
                Is.EqualTo(new[] { "🇺🇸 US", "🇯🇵 JP", "🇭🇰 HK", "🇸🇬 SG" }));
            Assert.That(
                plan.RegionAutoOutbounds.Select(outbound => outbound.Tag),
                Is.EqualTo(new[]
                {
                    "🇺🇸 US AUTO",
                    "🇯🇵 JP AUTO",
                    "🇭🇰 HK AUTO",
                    "🇸🇬 SG AUTO"
                }));
            Assert.That(plan.MainOutbound.Default, Is.EqualTo(unitedStates));
            Assert.That(
                plan.MainOutbound.Outbounds,
                Does.Not.Contain("⚡ AUTO"));
        });

        foreach (SelectorOutbound region in plan.RegionOutbounds)
        {
            UrlTestOutbound auto = plan.RegionAutoOutbounds.Single(candidate =>
                candidate.Tag == $"{region.Tag} AUTO");
            Assert.Multiple(() =>
            {
                Assert.That(region.Default, Is.EqualTo(auto.Tag));
                Assert.That(region.Outbounds[0], Is.EqualTo(auto.Tag));
                Assert.That(region.InterruptExistConnections, Is.True);
                Assert.That(region.Outbounds.Skip(1), Is.EqualTo(auto.Outbounds));
                Assert.That(
                    auto.Outbounds,
                    Is.All.Matches<string>(leafTags.Contains));
                Assert.That(auto.Outbounds, Has.Count.GreaterThanOrEqualTo(2));
                Assert.That(auto.InterruptExistConnections, Is.False);
            });
        }

        foreach (SelectorOutbound service in plan.ServiceOutbounds)
        {
            ServiceDefinition definition = BoxForgeProfile.Default.Services.Single(
                candidate => candidate.Name == service.Tag);
            if (definition.DefaultRegion is not null)
            {
                Assert.That(
                    service.Default,
                    Is.EqualTo(RegionName(definition.DefaultRegion)));
                Assert.That(service.Default, Does.Not.EndWith(" AUTO"));
            }
        }
    }

    [Test]
    public void SingleNodeDoesNotCreateAutoGroupsAndBecomesMainDefault()
    {
        NodeCatalog nodes = CreateCatalog("美国 01");

        ProfilePlan plan = Plan(nodes, BoxForgeProfile.Default);

        Assert.Multiple(() =>
        {
            Assert.That(plan.RegionAutoOutbounds, Is.Empty);
            Assert.That(plan.RegionOutbounds, Is.Empty);
            Assert.That(plan.MainOutbound.Default, Is.EqualTo("美国 01"));
            Assert.That(
                plan.MainOutbound.Outbounds,
                Is.EqualTo(new[] { "美国 01", SingboxTags.DirectOutbound }));
        });
    }

    [Test]
    public void MultipleNodesWithoutUnitedStatesPreferTheFirstRegionGroup()
    {
        ProfilePlan plan = Plan(CreateCatalog("日本 01", "日本 02"), BoxForgeProfile.Default);
        string japan = RegionName("jp");

        Assert.Multiple(() =>
        {
            Assert.That(plan.MainOutbound.Default, Is.EqualTo(japan));
            Assert.That(plan.MainOutbound.Outbounds, Does.Contain(japan));
            Assert.That(plan.MainOutbound.Outbounds, Does.Not.Contain("⚡ AUTO"));
        });
    }

    [Test]
    public void MultipleUnmatchedNodesFallBackToTheFirstLeafNode()
    {
        ProfilePlan plan = Plan(CreateCatalog("其他 01", "其他 02"), BoxForgeProfile.Default);

        Assert.Multiple(() =>
        {
            Assert.That(plan.RegionOutbounds, Is.Empty);
            Assert.That(plan.RegionAutoOutbounds, Is.Empty);
            Assert.That(plan.MainOutbound.Default, Is.EqualTo("其他 01"));
            Assert.That(
                plan.MainOutbound.Outbounds,
                Is.EqualTo(new[]
                {
                    "其他 01",
                    "其他 02",
                    SingboxTags.DirectOutbound
                }));
        });
    }

    [Test]
    public void SinglePreferredRegionLeavesRemainServiceTargetsWithoutGeneratingGroups()
    {
        ProfilePlan plan = Plan(CreateCatalog("美国 01", "日本 01", "日本 02", "香港 01"), BoxForgeProfile.Default);
        Assert.That(plan.RegionOutbounds.Select(group => group.Tag), Is.EqualTo(new[] { "🇯🇵 JP" }));
        Assert.That(plan.MainOutbound.Default, Is.EqualTo("🇯🇵 JP"), "Preserve documented generated-group fallback");
        foreach (SelectorOutbound service in plan.ServiceOutbounds)
        {
            ServiceDefinition definition = BoxForgeProfile.Default.Services.Single(candidate => candidate.Name == service.Tag);
            string expected = definition.DefaultRegion switch
            {
                "us" => "美国 01",
                "hk" => "香港 01",
                _ => SingboxTags.MainProxyGroup
            };
            Assert.That(service.Default, Is.EqualTo(expected));
            Assert.That(service.Outbounds, Does.Contain(expected));
            Assert.That(service.Outbounds, Does.Not.Contain("🇺🇸 US").And.Not.Contain("🇭🇰 HK"));
        }
    }

    [Test]
    public void MissingPreferredRegionsUseMainNotAnUnrelatedRegion()
    {
        ProfilePlan plan = Plan(CreateCatalog("日本 01", "日本 02"), BoxForgeProfile.Default);
        Assert.That(plan.ServiceOutbounds.Select(service => service.Default), Is.All.EqualTo(SingboxTags.MainProxyGroup));
    }

    [Test]
    public void EmptyInventoryProducesOnlyValidDirectMainAndServiceReferences()
    {
        ProfilePlan plan = Plan(CreateCatalog(), BoxForgeProfile.Default);
        Assert.That(plan.MainOutbound.Default, Is.EqualTo(SingboxTags.DirectOutbound));
        Assert.That(plan.RegionOutbounds, Is.Empty);
        Assert.That(plan.RegionAutoOutbounds, Is.Empty);
        Assert.That(plan.ServiceOutbounds.Select(service => service.Default), Is.All.EqualTo(SingboxTags.MainProxyGroup));
    }

    private static ProfilePlan Plan(NodeCatalog nodes, BoxForgeProfile profile) =>
        SingboxGroupEmitter.Emit(ProfilePlanner.PlanGroups(nodes, profile, probeInterval: null));

    private static NodeCatalog CreateCatalog(params string[] names)
    {
        if (names.Length == 0) return new NodeCatalog([], names);
        var source = new ClashParser().Parse(ProfileConfigurationTests.Yaml(names))!;
        return new NodeCatalog(source.Proxies.Select(ProxyNodeParser.Parse).ToArray(), names);
    }

    private static string RegionName(string regionId) =>
        BoxForgeProfile.Default.Regions.Single(definition =>
            definition.Id == regionId).DisplayName;
}
