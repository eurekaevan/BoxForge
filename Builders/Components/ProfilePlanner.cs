using Microsoft.Extensions.Options;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public class ProfilePlanner(IOptions<SingboxOptions> options)
{
    private readonly SingboxOptions singboxOptions = options.Value;

    public ProfilePlan Plan(NodeCatalog nodes)
    {
        var generatedRegions = new Dictionary<RegionId, string>();
        var regionOutbounds = BuildRegionOutbounds(nodes, generatedRegions);
        var mainOutbound = BuildMainOutbound(nodes, generatedRegions);
        var serviceOutbounds = BuildServiceOutbounds(nodes, generatedRegions);
        var directOutbound = new DirectOutbound
        {
            Tag = singboxOptions.Direct,
            DomainResolver = "local"
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

        foreach (var (regionId, definition) in ProfileDefinitions.Regions)
        {
            var matchedNodes = nodes.Names
                .Where(name => definition.Pattern.IsMatch(name))
                .ToList();

            if (matchedNodes.Count < 2)
            {
                continue;
            }

            generatedRegions[regionId] = definition.DisplayName;
            outbounds.Add(new SelectorOutbound
            {
                Tag = definition.DisplayName,
                Outbounds = matchedNodes,
                Default = matchedNodes.FirstOrDefault(),
                InterruptExistConnections = true
            });
        }

        return outbounds;
    }

    private SelectorOutbound BuildMainOutbound(
        NodeCatalog nodes,
        Dictionary<RegionId, string> generatedRegions)
    {
        var groupOptions = new List<string>(generatedRegions.Values);
        groupOptions.AddRange(nodes.Names);
        groupOptions.Add(singboxOptions.Direct);

        return new SelectorOutbound
        {
            Tag = singboxOptions.MainProxyGroup,
            Outbounds = groupOptions,
            Default = generatedRegions.Values.FirstOrDefault()
                ?? nodes.Names.FirstOrDefault()
                ?? singboxOptions.Direct,
            InterruptExistConnections = true
        };
    }

    private List<SelectorOutbound> BuildServiceOutbounds(
        NodeCatalog nodes,
        Dictionary<RegionId, string> generatedRegions)
    {
        var groupOptions = new List<string> { singboxOptions.MainProxyGroup };
        groupOptions.AddRange(generatedRegions.Values);
        groupOptions.AddRange(nodes.Names);
        groupOptions.Add(singboxOptions.Direct);

        var outbounds = new List<SelectorOutbound>();
        foreach (var service in ProfileDefinitions.Services)
        {
            var defaultSelection = singboxOptions.MainProxyGroup;
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
                Outbounds = groupOptions,
                Default = defaultSelection,
                InterruptExistConnections = true
            });
        }

        return outbounds;
    }
}
