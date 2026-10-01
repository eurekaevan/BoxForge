using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public static class ProfilePlanner
{
    public static ProfilePlan Plan(NodeCatalog nodes)
    {
        var inventory = ProfileDefinitions.Regions.Select(definition => new RegionInventory(
            definition, nodes.Names.Where(name => definition.Pattern.IsMatch(name)).ToArray())).ToArray();
        var regionAutoOutbounds = new List<UrlTestOutbound>();
        var regionOutbounds = BuildRegionOutbounds(
            inventory,
            regionAutoOutbounds);
        var mainOutbound = BuildMainOutbound(nodes, regionOutbounds);
        var serviceOutbounds = BuildServiceOutbounds(nodes, inventory, regionOutbounds);
        var directOutbound = new DirectOutbound { Tag = SingboxTags.DirectOutbound };

        return new ProfilePlan(
            mainOutbound,
            regionOutbounds,
            regionAutoOutbounds,
            serviceOutbounds,
            directOutbound);
    }

    private static List<SelectorOutbound> BuildRegionOutbounds(
        IReadOnlyList<RegionInventory> inventory,
        List<UrlTestOutbound> regionAutoOutbounds)
    {
        var outbounds = new List<SelectorOutbound>();

        foreach (RegionInventory region in inventory)
        {
            if (region.Leaves.Count < 2)
            {
                continue;
            }

            string autoTag = $"{region.Definition.DisplayName} AUTO";
            regionAutoOutbounds.Add(new UrlTestOutbound
            {
                Tag = autoTag,
                Outbounds = [.. region.Leaves]
            });
            outbounds.Add(new SelectorOutbound
            {
                Tag = region.Definition.DisplayName,
                Outbounds = [autoTag, .. region.Leaves],
                Default = autoTag,
                InterruptExistConnections = true
            });
        }

        return outbounds;
    }

    private static SelectorOutbound BuildMainOutbound(
        NodeCatalog nodes,
        List<SelectorOutbound> regionOutbounds)
    {
        var groupOptions = regionOutbounds
            .Select(outbound => outbound.Tag)
            .ToList();
        groupOptions.AddRange(nodes.Names);
        groupOptions.Add(SingboxTags.DirectOutbound);

        string fallbackSelection = regionOutbounds.Count > 0
            ? regionOutbounds[0].Tag
            : nodes.Names.Count > 0
                ? nodes.Names[0]
                : SingboxTags.DirectOutbound;
        string defaultSelection = regionOutbounds.FirstOrDefault(outbound =>
                outbound.Tag == GetRegionName(RegionId.UnitedStates))?.Tag
            ?? fallbackSelection;

        return new SelectorOutbound
        {
            Tag = SingboxTags.MainProxyGroup,
            Outbounds = groupOptions,
            Default = defaultSelection,
            InterruptExistConnections = true
        };
    }

    private static string GetRegionName(RegionId regionId) =>
        ProfileDefinitions.Regions.Single(definition =>
            definition.Id == regionId).DisplayName;

    private static List<SelectorOutbound> BuildServiceOutbounds(
        NodeCatalog nodes,
        IReadOnlyList<RegionInventory> inventory,
        IReadOnlyList<SelectorOutbound> regionOutbounds)
    {
        var groupOptions = new List<string> { SingboxTags.MainProxyGroup };
        groupOptions.AddRange(regionOutbounds.Select(outbound => outbound.Tag));
        groupOptions.AddRange(nodes.Names);
        groupOptions.Add(SingboxTags.DirectOutbound);

        var outbounds = new List<SelectorOutbound>();
        foreach (var service in ProfileDefinitions.Services)
        {
            string defaultSelection = inventory.FirstOrDefault(region =>
                region.Definition.Id == service.DefaultRegion)?.Target ?? SingboxTags.MainProxyGroup;

            outbounds.Add(new SelectorOutbound
            {
                Tag = service.Name,
                Outbounds = [.. groupOptions],
                Default = defaultSelection,
                InterruptExistConnections = true
            });
        }

        return outbounds;
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
