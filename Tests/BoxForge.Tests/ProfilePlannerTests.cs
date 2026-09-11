using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProfilePlannerTests
{
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
        string unitedStates = RegionName(RegionId.UnitedStates);

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
                Assert.That(auto.InterruptExistConnections, Is.Null);
            });
        }

        foreach (SelectorOutbound service in plan.ServiceOutbounds)
        {
            ServiceDefinition definition = ProfileDefinitions.Services.Single(
                candidate => candidate.Name == service.Tag);
            if (definition.DefaultRegion.HasValue)
            {
                Assert.That(
                    service.Default,
                    Is.EqualTo(RegionName(definition.DefaultRegion.Value)));
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
        string japan = RegionName(RegionId.Japan);

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

        return new NodeCatalog(outbounds, names, []);
    }

    private static string RegionName(RegionId regionId) =>
        ProfileDefinitions.Regions.Single(definition =>
            definition.Id == regionId).DisplayName;
}
