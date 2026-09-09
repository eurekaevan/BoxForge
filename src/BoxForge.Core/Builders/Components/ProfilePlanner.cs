using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public static class ProfilePlanner
{
    public static ProfilePlan Plan(NodeCatalog nodes)
    {
        var generatedRegions = new Dictionary<RegionId, string>();
        var regionAutoOutbounds = new List<UrlTestOutbound>();
        var regionOutbounds = BuildRegionOutbounds(
            nodes,
            generatedRegions,
            regionAutoOutbounds);
        UrlTestOutbound? autoOutbound = BuildAutoOutbound(nodes.Names);
        var mainOutbound = BuildMainOutbound(
            nodes,
            regionOutbounds,
            autoOutbound);
        var serviceOutbounds = BuildServiceOutbounds(nodes, generatedRegions);
        var directOutbound = new DirectOutbound
        {
            Tag = SingboxTags.DirectOutbound,
            DomainResolver = SingboxTags.LocalDns
        };

        return new ProfilePlan(
            mainOutbound,
            autoOutbound,
            regionOutbounds,
            regionAutoOutbounds,
            serviceOutbounds,
            directOutbound);
    }

    private static List<SelectorOutbound> BuildRegionOutbounds(
        NodeCatalog nodes,
        Dictionary<RegionId, string> generatedRegions,
        List<UrlTestOutbound> regionAutoOutbounds)
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

            string autoTag = $"{definition.DisplayName} AUTO";
            generatedRegions[definition.Id] = definition.DisplayName;
            regionAutoOutbounds.Add(new UrlTestOutbound
            {
                Tag = autoTag,
                Outbounds = [.. matchedNodes]
            });
            outbounds.Add(new SelectorOutbound
            {
                Tag = definition.DisplayName,
                Outbounds = [autoTag, .. matchedNodes],
                Default = autoTag,
                InterruptExistConnections = true
            });
        }

        return outbounds;
    }

    private static SelectorOutbound BuildMainOutbound(
        NodeCatalog nodes,
        List<SelectorOutbound> regionOutbounds,
        UrlTestOutbound? autoOutbound)
    {
        var groupOptions = regionOutbounds
            .Select(outbound => outbound.Tag)
            .ToList();
        if (autoOutbound != null)
        {
            groupOptions.Add(autoOutbound.Tag);
        }
        groupOptions.AddRange(nodes.Names);
        groupOptions.Add(SingboxTags.DirectOutbound);

        return new SelectorOutbound
        {
            Tag = SingboxTags.MainProxyGroup,
            Outbounds = groupOptions,
            Default = nodes.Names.Count >= 2
                ? regionOutbounds.FirstOrDefault(outbound =>
                        outbound.Tag == GetRegionName(RegionId.UnitedStates))?.Tag
                    ?? autoOutbound!.Tag
                : nodes.Names.Count == 1
                    ? nodes.Names[0]
                    : SingboxTags.DirectOutbound,
            InterruptExistConnections = true
        };
    }

    private static UrlTestOutbound? BuildAutoOutbound(
        IReadOnlyList<string> nodeNames) =>
        nodeNames.Count >= 2
            ? new UrlTestOutbound
            {
                Tag = SingboxTags.AutoProxyGroup,
                Outbounds = [.. nodeNames]
            }
            : null;

    private static string GetRegionName(RegionId regionId) =>
        ProfileDefinitions.Regions.Single(definition =>
            definition.Id == regionId).DisplayName;

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
