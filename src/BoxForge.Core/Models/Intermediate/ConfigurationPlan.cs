using BoxForge.Configuration;
using BoxForge.Exceptions;

namespace BoxForge.Models.Intermediate;

internal sealed record ConfigurationPlan(
    PlatformProfile Platform,
    IReadOnlyList<string> LeafTags,
    ProfileGroupPlan Groups,
    DnsPlan Dns,
    RoutePlan Route,
    bool TailscaleEndpointEnabled,
    string TaildropDirectory,
    bool ApiServiceEnabled,
    IReadOnlyList<HttpClientPlan> HttpClients)
{
    internal void Validate()
    {
        Groups.Validate();
        Dns.Validate();
        Route.Validate();
        var diagnostics = new List<ConfigDiagnostic>();
        if (Dns.AddressFamily != Platform.AddressFamily)
            diagnostics.Add(new("IR001", "plan.dns.address_family", "DNS 地址族必须与平台策略一致。"));
        if (Route.AddressFamily != Platform.AddressFamily)
            diagnostics.Add(new("IR001", "plan.route.address_family", "路由地址族必须与平台策略一致。"));
        if (Dns.SelectionMode != Platform.DnsSelectionMode)
            diagnostics.Add(new("IR001", "plan.dns.selection_mode", "DNS 选择模式必须与平台策略一致。"));
        if (Route.UseDirectForwarding != Platform.UseDirectForwarding)
            diagnostics.Add(new("IR001", "plan.route.use_direct_forwarding", "直连转发必须与平台策略一致。"));
        for (int index = 0; index < Groups.Probes.Count; index++)
        {
            if (Groups.Probes[index].Interval != Platform.UrlTestInterval)
                diagnostics.Add(new("IR001", $"plan.groups.probes[{index}].interval", "探测间隔必须与平台策略一致。"));
        }

        var leaves = LeafTags.ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < Groups.Leaves.Count; index++)
            Require(leaves, Groups.Leaves[index], "IR002", $"plan.groups.leaves[{index}]");
        var targets = new HashSet<string>(leaves, StringComparer.Ordinal) { Groups.Main.Name, Groups.DirectName };
        targets.UnionWith(Groups.Regions.Select(group => group.Name));
        targets.UnionWith(Groups.Probes.Select(group => group.Name));
        targets.UnionWith(Groups.Services.Select(group => group.Name));
        if (Platform.UseDirectForwarding)
            targets.Add(SingboxTags.BridgeOutbound);
        if (TailscaleEndpointEnabled)
            targets.Add(SingboxTags.TailscaleEndpoint);
        if (Dns.TailscaleEnabled && !TailscaleEndpointEnabled)
            diagnostics.Add(new("IR004", "plan.dns.tailscale_enabled", "Tailscale DNS 需要已声明的 endpoint。"));
        if (Route.TailscaleEnabled && !TailscaleEndpointEnabled)
            diagnostics.Add(new("IR004", "plan.route.tailscale_enabled", "Tailscale 路由需要已声明的 endpoint。"));
        Require(targets, Route.FinalOutbound, "IR003", "plan.route.final_outbound");
        Require(targets, SingboxTags.DirectOutbound, "IR003", "plan.route.direct");
        for (int index = 0; index < Route.PriorityServices.Count; index++)
            Require(targets, Route.PriorityServices[index].Name, "IR003", $"plan.route.priority_services[{index}].name");
        for (int index = 0; index < Route.OtherServices.Count; index++)
            Require(targets, Route.OtherServices[index].Name, "IR003", $"plan.route.other_services[{index}].name");
        for (int index = 0; index < Dns.Resolvers.Count; index++)
        {
            if (Dns.Resolvers[index].Detour is { } detour)
                Require(targets, detour, "IR003", $"plan.dns.resolvers[{index}].detour");
        }

        var ruleSets = Route.RuleSets.SelectMany(source => source.Tags).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < Dns.BlockedRuleSets.Count; index++)
            Require(ruleSets, Dns.BlockedRuleSets[index], "IR005", $"plan.dns.blocked_rule_sets[{index}]");
        for (int poolIndex = 0; poolIndex < Dns.Pools.Count; poolIndex++)
        {
            if (Dns.Pools[poolIndex].RuleSets is { } scope)
            {
                for (int index = 0; index < scope.Count; index++)
                    Require(ruleSets, scope[index], "IR005", $"plan.dns.pools[{poolIndex}].rule_sets[{index}]");
            }
        }
        var resolvers = Dns.Resolvers.Select(resolver => resolver.Name).ToHashSet(StringComparer.Ordinal);
        Require(resolvers, SingboxTags.DirectAliDns, "IR006", "plan.route.default_domain_resolver");
        for (int index = 0; index < HttpClients.Count; index++)
        {
            HttpClientPlan client = HttpClients[index];
            if (client.Detour is { } detour)
                Require(targets, detour, "IR003", $"plan.http_clients[{index}].detour");
            Require(resolvers, client.Resolver, "IR006", $"plan.http_clients[{index}].resolver");
        }
        var clients = HttpClients.Select(client => client.Name).ToHashSet(StringComparer.Ordinal);
        Require(clients, HttpClientTags.RuleSetProxy, "IR007", "plan.route.default_http_client");
        if (ApiServiceEnabled)
            Require(clients, HttpClientTags.DashboardDirect, "IR007", "plan.services.dashboard.http_client");
        if (diagnostics.Count > 0)
            throw new ConfigValidationException(diagnostics);

        void Require(HashSet<string> declarations, string reference, string code, string path)
        {
            if (!declarations.Contains(reference))
                diagnostics.Add(new(code, path, $"引用了未声明的目标 '{reference}'。"));
        }
    }
}

internal sealed record HttpClientPlan(string Name, string? Detour, string Resolver);
