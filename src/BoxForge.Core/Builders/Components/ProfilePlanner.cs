using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal static class ProfilePlanner
{
    internal static ProfileGroupPlan PlanGroups(NodeCatalog nodes, ResolvedProfile profile)
    {
        var inventory = profile.Regions.Select(definition => new RegionInventory(
            definition, nodes.Names.Where(name => definition.Pattern.IsMatch(name)).ToArray())).ToArray();
        var probes = new List<ProbeGroupPlan>();
        var regions = BuildRegionGroups(
            inventory,
            probes, profile.Tuning);
        var main = BuildMainGroup(nodes, regions, profile);
        var services = BuildServiceGroups(nodes, inventory, regions, profile);
        return new ProfileGroupPlan(
            nodes.Names.ToArray(),
            SingboxTags.DirectOutbound,
            main,
            regions,
            probes,
            services);
    }

    private static List<SelectionGroupPlan> BuildRegionGroups(
        IReadOnlyList<RegionInventory> inventory,
        List<ProbeGroupPlan> probes, TuningProfile policies)
    {
        var groups = new List<SelectionGroupPlan>();

        foreach (RegionInventory region in inventory)
        {
            if (region.Leaves.Count < 2)
            {
                continue;
            }

            string autoTag = $"{region.Definition.DisplayName} AUTO";
            probes.Add(new ProbeGroupPlan(
                autoTag, region.Leaves.ToArray(),
                policies.UrlTestUrl, policies.UrlTestTolerance, policies.UrlTestIdleTimeout, policies.UrlTestInterval));
            groups.Add(new SelectionGroupPlan(
                region.Definition.DisplayName, [autoTag, .. region.Leaves], autoTag));
        }

        return groups;
    }

    private static SelectionGroupPlan BuildMainGroup(
        NodeCatalog nodes,
        List<SelectionGroupPlan> regions,
        ResolvedProfile profile)
    {
        List<string> groupOptions =
        [
            .. regions.Select(group => group.Name),
            .. nodes.Names,
            SingboxTags.DirectOutbound
        ];

        string fallbackSelection = regions.Count > 0
            ? regions[0].Name
            : nodes.Names.Count > 0
                ? nodes.Names[0]
                : SingboxTags.DirectOutbound;
        string? preferredRegionName = regions.Count == 0
            ? null
            : profile.Regions.Single(region => region.Id == profile.Route.MainDefaultRegion).DisplayName;
        string defaultSelection = regions.FirstOrDefault(group => group.Name == preferredRegionName)?.Name
            ?? fallbackSelection;

        return new SelectionGroupPlan(SingboxTags.MainProxyGroup, groupOptions, defaultSelection);
    }

    private static List<SelectionGroupPlan> BuildServiceGroups(
        NodeCatalog nodes,
        IReadOnlyList<RegionInventory> inventory,
        IReadOnlyList<SelectionGroupPlan> regions,
        ResolvedProfile profile)
    {
        List<string> groupOptions =
        [
            SingboxTags.MainProxyGroup,
            .. regions.Select(group => group.Name),
            .. nodes.Names,
            SingboxTags.DirectOutbound
        ];

        var groups = new List<SelectionGroupPlan>();
        foreach (var service in profile.Services)
        {
            string defaultSelection = inventory.FirstOrDefault(region =>
                region.Definition.Id == service.DefaultRegion)?.Target ?? SingboxTags.MainProxyGroup;

            groups.Add(new SelectionGroupPlan(service.Name, groupOptions.ToArray(), defaultSelection));
        }

        return groups;
    }

    private sealed record RegionInventory(RegionDefinition Definition, IReadOnlyList<string> Leaves)
    {
        public string? Target => Leaves.Count switch
        {
            0 => null,
            1 => Leaves[0],
            _ => Definition.DisplayName
        };
    }
}
