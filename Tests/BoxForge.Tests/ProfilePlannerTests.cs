using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProfilePlannerTests
{
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
                ProfileDefinitions.Regions.Single(region => region.Id == "sg").Pattern.Options);
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
        using Stream source = typeof(ProfileDefinitions).Assembly.GetManifestResourceStream(
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

        ProfilePlan plan = ProfilePlanner.Plan(nodes);
        HashSet<string> leafTags = nodes.Outbounds
            .Select(outbound => outbound.Tag)
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
            ServiceDefinition definition = ProfileDefinitions.Services.Single(
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

        ProfilePlan plan = ProfilePlanner.Plan(nodes);

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
        ProfilePlan plan = ProfilePlanner.Plan(CreateCatalog("日本 01", "日本 02"));
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
        ProfilePlan plan = ProfilePlanner.Plan(CreateCatalog("其他 01", "其他 02"));

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
        ProfilePlan plan = ProfilePlanner.Plan(CreateCatalog("美国 01", "日本 01", "日本 02", "香港 01"));
        Assert.That(plan.RegionOutbounds.Select(group => group.Tag), Is.EqualTo(new[] { "🇯🇵 JP" }));
        Assert.That(plan.MainOutbound.Default, Is.EqualTo("🇯🇵 JP"), "Preserve documented generated-group fallback");
        foreach (SelectorOutbound service in plan.ServiceOutbounds)
        {
            ServiceDefinition definition = ProfileDefinitions.Services.Single(candidate => candidate.Name == service.Tag);
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
        ProfilePlan plan = ProfilePlanner.Plan(CreateCatalog("日本 01", "日本 02"));
        Assert.That(plan.ServiceOutbounds.Select(service => service.Default), Is.All.EqualTo(SingboxTags.MainProxyGroup));
    }

    [Test]
    public void EmptyInventoryProducesOnlyValidDirectMainAndServiceReferences()
    {
        ProfilePlan plan = ProfilePlanner.Plan(CreateCatalog());
        Assert.That(plan.MainOutbound.Default, Is.EqualTo(SingboxTags.DirectOutbound));
        Assert.That(plan.RegionOutbounds, Is.Empty);
        Assert.That(plan.RegionAutoOutbounds, Is.Empty);
        Assert.That(plan.ServiceOutbounds.Select(service => service.Default), Is.All.EqualTo(SingboxTags.MainProxyGroup));
    }

    private static NodeCatalog CreateCatalog(params string[] names)
    {
        List<ProxyOutbound> outbounds = names.Select((name, index) =>
            (ProxyOutbound)new ShadowsocksOutbound
            {
                Tag = name,
                Server = $"node-{index}.example.com",
                ServerPort = 443,
                Method = "aes-128-gcm",
                Password = "test-only"
            }).ToList();

        return new NodeCatalog(outbounds, names);
    }

    private static string RegionName(string regionId) =>
        ProfileDefinitions.Regions.Single(definition =>
            definition.Id == regionId).DisplayName;
}
