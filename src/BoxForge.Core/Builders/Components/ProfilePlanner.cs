using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public static class ProfilePlanner
{
    public static ProfilePlan Plan(NodeCatalog nodes)
    {
        var generatedRegions = new Dictionary<RegionId, string>();
        var regionOutbounds = BuildRegionOutbounds(nodes, generatedRegions);
        var mainOutbound = BuildMainOutbound(nodes, regionOutbounds);
        var serviceOutbounds = BuildServiceOutbounds(nodes, generatedRegions);
        var directOutbound = new DirectOutbound
        {
            Tag = SingboxTags.DirectOutbound,
            DomainResolver = SingboxTags.LocalDns
        };

        return new ProfilePlan(
            mainOutbound,
            regionOutbounds,
            serviceOutbounds,
            directOutbound);
    }

    private static List<SelectorOutbound> BuildRegionOutbounds(
        NodeCatalog nodes,
        Dictionary<RegionId, string> generatedRegions)
    {
        var outbounds = new List<SelectorOutbound>();

        foreach (RegionDefinition definition in ProfileDefinitions.Regions)
        {
            var matchedNodes = nodes.Names
                .Where(name => definition.Pattern.IsMatch(name))
                .ToList();

            if (matchedNodes.Count < 2)
            {
                continue;
            }

            generatedRegions[definition.Id] = definition.DisplayName;
            outbounds.Add(new SelectorOutbound
            {
                Tag = definition.DisplayName,
                Outbounds = matchedNodes,
                Default = matchedNodes[0],
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

        return new SelectorOutbound
        {
            Tag = SingboxTags.MainProxyGroup,
            Outbounds = groupOptions,
            Default = regionOutbounds.Count > 0
                ? regionOutbounds[0].Tag
                : nodes.Names.Count > 0
                    ? nodes.Names[0]
                    : SingboxTags.DirectOutbound,
            InterruptExistConnections = true
        };
    }

    private static List<SelectorOutbound> BuildServiceOutbounds(
        NodeCatalog nodes,
        Dictionary<RegionId, string> generatedRegions)
    {
        var groupOptions = new List<string> { SingboxTags.MainProxyGroup };
        groupOptions.AddRange(generatedRegions.Values);
        groupOptions.AddRange(nodes.Names);
        groupOptions.Add(SingboxTags.DirectOutbound);

        var outbounds = new List<SelectorOutbound>();
        foreach (var service in ProfileDefinitions.Services)
        {
            var defaultSelection = SingboxTags.MainProxyGroup;
            if (service.DefaultRegion.HasValue
                && generatedRegions.TryGetValue(
                    service.DefaultRegion.Value,
                    out var generatedRegionName))
            {
                defaultSelection = generatedRegionName;
            }

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
}
