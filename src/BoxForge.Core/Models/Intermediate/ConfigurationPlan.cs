using BoxForge.Configuration;
using System.Collections.Immutable;
using BoxForge.Exceptions;
using BoxForge.Models;

namespace BoxForge.Models.Intermediate;

internal sealed record ConfigurationPlan(
    TargetPlatform Platform,
    AddressFamilyPolicy AddressFamily,
    DnsResolverSelectionMode SelectionMode,
    bool UseDirectForwarding,
    string ProbeInterval,
    TunPlan Tun,
    IReadOnlyList<ProxyNode> Nodes,
    ProfileGroupPlan Groups,
    DnsPlan Dns,
    RoutePlan Route,
    bool TailscaleEndpointEnabled,
    string TaildropDirectory,
    bool ApiServiceEnabled,
    IReadOnlyList<HttpClientPlan> HttpClients)
{
    internal required ImmutableDictionary<string, DomainResolverPlan> NodeResolvers { get; init; }
    internal required DomainResolverPlan DirectResolver { get; init; }

    internal void Validate()
    {
        var diagnostics = new List<ConfigDiagnostic>();
        Groups.CollectDiagnostics(diagnostics);
        Dns.CollectDiagnostics(diagnostics);
        Route.CollectDiagnostics(diagnostics);
        if (Dns.AddressFamily != AddressFamily)
            diagnostics.Add(new("IR001", "plan.dns.address_family", "DNS 地址族必须与平台策略一致。"));
        if (Route.AddressFamily != AddressFamily)
            diagnostics.Add(new("IR001", "plan.route.address_family", "路由地址族必须与平台策略一致。"));
        if (Dns.SelectionMode != SelectionMode)
            diagnostics.Add(new("IR001", "plan.dns.selection_mode", "DNS 选择模式必须与平台策略一致。"));
        if (Route.UseDirectForwarding != UseDirectForwarding)
            diagnostics.Add(new("IR001", "plan.route.use_direct_forwarding", "直连转发必须与平台策略一致。"));
        for (int index = 0; index < Groups.Probes.Count; index++)
        {
            if (Groups.Probes[index].Interval != ProbeInterval)
                diagnostics.Add(new("IR001", $"plan.groups.probes[{index}].interval", "探测间隔必须与平台策略一致。"));
        }

        var leaves = Nodes.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < Groups.Leaves.Count; index++)
            Require(leaves, Groups.Leaves[index], "IR002", $"plan.groups.leaves[{index}]");
        var targets = new HashSet<string>(leaves, StringComparer.Ordinal) { Groups.Main.Name, Groups.DirectName };
        targets.UnionWith(Groups.Regions.Select(group => group.Name));
        targets.UnionWith(Groups.Probes.Select(group => group.Name));
        targets.UnionWith(Groups.Services.Select(group => group.Name));
        if (UseDirectForwarding)
            targets.Add(SingboxTags.BridgeOutbound);
        if (TailscaleEndpointEnabled)
            targets.Add(SingboxTags.TailscaleEndpoint);
        if (Dns.TailscaleEnabled && !TailscaleEndpointEnabled)
            diagnostics.Add(new("IR004", "plan.dns.tailscale_enabled", "Tailscale DNS 需要已声明的 endpoint。"));
        if (Route.TailscaleEnabled && !TailscaleEndpointEnabled)
            diagnostics.Add(new("IR004", "plan.route.tailscale_enabled", "Tailscale 路由需要已声明的 endpoint。"));
        Require(targets, Route.FinalOutbound, "IR003", "plan.route.final_outbound");
        Require(targets, SingboxTags.DirectOutbound, "IR003", "plan.route.direct");
        for (int index = 0; !Route.Rules.IsDefault && index < Route.Rules.Length; index++)
            RequireRouteReferences(Route.Rules[index], $"plan.route.rules[{index}]");
        for (int index = 0; index < Dns.Resolvers.Count; index++)
        {
            if (Dns.Resolvers[index].Detour is { } detour)
                Require(targets, detour, "IR003", $"plan.dns.resolvers[{index}].detour");
        }

        var ruleSets = Route.RuleSets.Where(source => !source.Tags.IsDefault).SelectMany(source => source.Tags).ToHashSet(StringComparer.Ordinal);
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
        for (int index = 0; !Dns.Rules.IsDefault && index < Dns.Rules.Length; index++)
            RequireDnsRuleSets(Dns.Rules[index], $"plan.dns.rules[{index}]");
        for (int index = 0; !Route.Rules.IsDefault && index < Route.Rules.Length; index++)
        {
            RouteRulePlan service = Route.Rules[index];
            if (service.Phase is not (RoutePhase.PriorityServices or RoutePhase.OtherServices) || service.RuleSet.IsDefaultOrEmpty)
                continue;
            if (!Dns.Pools.Any(pool => pool.RuleSets is { } scope && scope.SequenceEqual(service.RuleSet)
                && BoundToService(pool.PrimaryResolver) && BoundToService(pool.SecondaryResolver)))
                diagnostics.Add(new("IR011", $"plan.route.rules[{index}]", "服务 DNS 的匹配范围和主备解析器出口必须与业务路由一致。"));

            bool BoundToService(string name) => Dns.Resolvers.Count(resolver => resolver.Name == name && resolver.Detour == service.Outbound) == 1;
        }
        var resolvers = Dns.Resolvers.Select(resolver => resolver.Name).ToHashSet(StringComparer.Ordinal);
        ValidateResolver(DirectResolver, "plan.direct_resolver");
        Require(resolvers, DirectResolver.Server, "IR006", "plan.direct_resolver.server");
        if (DirectResolver != new DomainResolverPlan(SingboxTags.DirectAliDns, AddressFamily, true, true))
            diagnostics.Add(new("IR011", "plan.direct_resolver", "直连解析器必须保留平台地址族限制并优先 IPv4。"));
        ValidateResolver(Route.DefaultDomainResolver, "plan.route.default_domain_resolver");
        Require(resolvers, Route.DefaultDomainResolver.Server, "IR006", "plan.route.default_domain_resolver");
        int resolverIndex = 0;
        foreach (var (node, resolver) in NodeResolvers.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string path = $"plan.node_resolvers[{resolverIndex++}]";
            Require(leaves, node, "IR002", path);
            Require(resolvers, resolver.Server, "IR006", path + ".server");
            ValidateResolver(resolver, path);
        }
        for (int index = 0; index < Nodes.Count; index++)
        {
            ProxyNode node = Nodes[index];
            if (AddressFamily != AddressFamilyPolicy.Ipv4Only && node.AddressFamily == AddressFamilyPolicy.Ipv4Only
                && (!NodeResolvers.TryGetValue(node.Name, out var binding)
                    || binding != new DomainResolverPlan(SingboxTags.DirectAliDns, AddressFamilyPolicy.Ipv4Only, true)))
                diagnostics.Add(new("IR011", $"plan.nodes[{index}].domain_resolver", "来源 IPv4 限制必须绑定独立的 IPv4 解析器。"));
        }
        if (Route.DefaultDomainResolver != new DomainResolverPlan(SingboxTags.DirectAliDns, AddressFamily.ForControlPlane(), true))
            diagnostics.Add(new("IR011", "plan.route.default_domain_resolver", "默认解析器绑定必须符合平台地址族和控制面策略。"));
        for (int index = 0; index < HttpClients.Count; index++)
        {
            HttpClientPlan client = HttpClients[index];
            if (client.Detour is { } detour)
                Require(targets, detour, "IR003", $"plan.http_clients[{index}].detour");
            ValidateResolver(client.Resolver, $"plan.http_clients[{index}].resolver");
            Require(resolvers, client.Resolver.Server, "IR006", $"plan.http_clients[{index}].resolver");
        }
        var clients = HttpClients.Select(client => client.Name).ToHashSet(StringComparer.Ordinal);
        Require(clients, Route.DefaultHttpClient, "IR007", "plan.route.default_http_client");
        if (ApiServiceEnabled)
            Require(clients, HttpClientTags.DashboardDirect, "IR007", "plan.services.dashboard.http_client");
        ConfigValidationException.ThrowIfAny(diagnostics);

        void ValidateResolver(DomainResolverPlan resolver, string path)
        {
            if (!Enum.IsDefined(resolver.AddressFamily))
                diagnostics.Add(new("IR008", path + ".address_family", "解析器地址族无效。"));
        }

        void RequireDnsRuleSets(DnsRulePlan rule, string path)
        {
            if (!rule.RuleSet.IsDefault)
                for (int index = 0; index < rule.RuleSet.Length; index++)
                    Require(ruleSets, rule.RuleSet[index], "IR005", $"{path}.rule_set[{index}]");
            if (!rule.Rules.IsDefault)
                for (int index = 0; index < rule.Rules.Length; index++)
                    RequireDnsRuleSets(rule.Rules[index], $"{path}.rules[{index}]");
        }

        void RequireRouteReferences(RouteRulePlan rule, string path)
        {
            // The endpoint dependency already has a specific IR004 diagnostic above.
            if (rule.Outbound is { } outbound
                && !(outbound == SingboxTags.TailscaleEndpoint && Route.TailscaleEnabled && !TailscaleEndpointEnabled))
                Require(targets, outbound, "IR003", path + ".outbound");
            if (!rule.Rules.IsDefault)
                for (int index = 0; index < rule.Rules.Length; index++)
                    RequireRouteReferences(rule.Rules[index], $"{path}.rules[{index}]");
        }

        void Require(HashSet<string> declarations, string reference, string code, string path)
        {
            if (!declarations.Contains(reference))
                diagnostics.Add(new(code, path, "引用了未声明的目标。"));
        }
    }
}

internal sealed record HttpClientPlan(string Name, string? Detour, DomainResolverPlan Resolver);

internal sealed record DomainResolverPlan(string Server, AddressFamilyPolicy AddressFamily, bool DisableOptimisticCache, bool PreferIpv4 = false);

internal sealed record TunPlan(ImmutableArray<string> Addresses, bool? AutoRedirect, bool? MultiQueue, bool? StrictRoute);
