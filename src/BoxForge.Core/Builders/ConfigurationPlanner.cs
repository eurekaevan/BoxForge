using System.Collections.Immutable;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders;

internal static class ConfigurationPlanner
{
    internal static ConfigurationPlan Plan(NodeCatalog nodes, ResolvedProfile settings)
    {
        PlatformProfile platform = settings.Settings;
        for (int index = 0; index < nodes.Names.Count; index++)
            if (settings.IsReservedTag(nodes.Names[index]))
                throw new ConfigValidationException([
                    new ConfigDiagnostic("IR009", $"plan.nodes[{index}].name", "节点名称与保留名称冲突。")]);
        var groups = ProfilePlanner.PlanGroups(nodes, settings);
        var dns = DnsProfileBuilder.Plan(settings);
        var route = RouteProfileBuilder.Plan(settings);
        bool apiEnabled = settings.Features.Api.Enabled;
        string ruleSetDetour = groups.Regions.FirstOrDefault(region => region.Name == groups.Main.Preferred)?.Preferred
            ?? groups.Main.Preferred;
        var clients = new List<HttpClientPlan>
        {
            new(HttpClientTags.RuleSetProxy, ruleSetDetour, new DomainResolverPlan(SingboxTags.DirectAliDns, platform.AddressFamily, true))
        };
        if (apiEnabled)
            clients.Add(new HttpClientPlan(HttpClientTags.DashboardDirect, null, new DomainResolverPlan(SingboxTags.DirectAliDns, platform.AddressFamily, true)));
        return new ConfigurationPlan(settings.Platform, platform.AddressFamily, platform.DnsSelectionMode, platform.UrlTestInterval,
            new TunPlan(platform.TunAddresses, platform.AutoRedirect, platform.MultiQueue, platform.StrictRoute),
            nodes.Nodes.Select(node => OutboundTuningPolicy.Apply(node, platform)).ToArray(),
            groups, dns, route, settings.Features.Tailscale.Enabled, platform.TaildropDirectory, apiEnabled, clients)
        {
            DirectResolver = new DomainResolverPlan(SingboxTags.DirectAliDns, platform.AddressFamily, true, true),
            NodeResolvers = nodes.Nodes.Where(node => node.AddressFamily == AddressFamilyPolicy.Ipv4Only
                    && platform.AddressFamily == AddressFamilyPolicy.DualStack)
                .DistinctBy(node => node.Name, StringComparer.Ordinal)
                .ToImmutableDictionary(node => node.Name,
                    _ => new DomainResolverPlan(SingboxTags.DirectAliDns, AddressFamilyPolicy.Ipv4Only, true), StringComparer.Ordinal)
        };
    }
}
