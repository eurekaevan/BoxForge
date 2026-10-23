using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Configuration;
using System.Collections.Immutable;

namespace BoxForge.Models.Intermediate;

internal sealed record RoutePlan(
    AddressFamilyPolicy AddressFamily,
    bool TailscaleEnabled,
    bool UseDirectForwarding,
    string FinalOutbound,
    string DefaultHttpClient,
    DomainResolverPlan DefaultDomainResolver,
    IReadOnlyList<RuleSetPlan> RuleSets,
    ImmutableArray<RouteRulePlan> Rules)
{
    internal void Validate()
    {
        var diagnostics = new List<ConfigDiagnostic>();
        CollectDiagnostics(diagnostics);
        ConfigValidationException.ThrowIfAny(diagnostics);
    }

    internal void CollectDiagnostics(List<ConfigDiagnostic> diagnostics)
    {
        int initialDiagnostics = diagnostics.Count;
        if (!Enum.IsDefined(AddressFamily))
            diagnostics.Add(new("IR008", "plan.route.address_family", "路由地址族无效。"));
        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (int sourceIndex = 0; sourceIndex < RuleSets.Count; sourceIndex++)
        {
            RuleSetPlan source = RuleSets[sourceIndex];
            string path = $"plan.route.rule_sets[{sourceIndex}].tags";
            if (source.Tags.IsDefaultOrEmpty)
                diagnostics.Add(new("IR009", path, "路由规则集必须声明非空标签。"));
            if (!source.Tags.IsDefault)
                for (int index = 0; index < source.Tags.Length; index++)
                    if (string.IsNullOrWhiteSpace(source.Tags[index]) || !tags.Add(source.Tags[index]))
                        diagnostics.Add(new("IR009", $"{path}[{index}]", "路由规则集标签必须非空且唯一。"));
        }
        if (!tags.IsSupersetOf([RuleSetTags.Ads, RuleSetTags.Cn, RuleSetTags.CnIp]))
            diagnostics.Add(new("IR009", "plan.route.rule_sets", "路由计划缺少广告或国内规则集。"));
        var services = new HashSet<string>(StringComparer.Ordinal);
        RoutePhase previous = RoutePhase.DnsHijack;
        if (Rules.IsDefault)
            diagnostics.Add(new("IR008", "plan.route.rules", "路由规则数组必须初始化。"));
        else
        {
            for (int index = 0; index < Rules.Length; index++)
            {
                RouteRulePlan rule = Rules[index];
                string path = $"plan.route.rules[{index}]";
                if (!Enum.IsDefined(rule.Phase) || rule.Phase < previous || rule.Phase == RoutePhase.FinalOutbound)
                    diagnostics.Add(new("IR010", path + ".phase", "路由规则必须遵循声明的阶段顺序。"));
                previous = rule.Phase;
                ValidateRule(rule, path);
                if (rule.Phase is RoutePhase.PriorityServices or RoutePhase.OtherServices)
                {
                    if (rule.Action != RouteOperation.Route)
                        diagnostics.Add(new("IR008", path + ".action", "路由服务必须使用路由动作。"));
                    if (string.IsNullOrWhiteSpace(rule.Outbound) || !services.Add(rule.Outbound))
                        diagnostics.Add(new("IR009", path + ".outbound", "路由服务出口必须非空且唯一。"));
                    if (rule.RuleSet.IsDefaultOrEmpty)
                        diagnostics.Add(new("IR008", path + ".rule_set", "路由服务必须限定规则集。"));
                }
            }
        }

        if (diagnostics.Count == initialDiagnostics)
            ValidatePolicy(diagnostics);

        void ValidateRule(RouteRulePlan rule, string path)
        {
            if (rule.Action is { } action && !Enum.IsDefined(action))
                diagnostics.Add(new("IR008", path + ".action", "路由规则动作无效。"));
            if (rule.Mode is { } mode && !Enum.IsDefined(mode))
                diagnostics.Add(new("IR008", path + ".mode", "路由逻辑匹配模式无效。"));
            if (rule.Mode is not null && rule.Rules.IsDefaultOrEmpty)
                diagnostics.Add(new("IR008", path + ".rules", "路由逻辑规则必须包含子条件。"));
            if (rule.Mode is null && !rule.Rules.IsDefault)
                diagnostics.Add(new("IR008", path + ".mode", "路由子条件必须指定逻辑匹配模式。"));
            if (!rule.RuleSet.IsDefault)
                for (int index = 0; index < rule.RuleSet.Length; index++)
                    if (!tags.Contains(rule.RuleSet[index]))
                        diagnostics.Add(new("IR005", $"{path}.rule_set[{index}]", "路由规则引用了未声明的规则集。"));
            if (!rule.Rules.IsDefault)
                for (int index = 0; index < rule.Rules.Length; index++)
                    ValidateRule(rule.Rules[index], $"{path}.rules[{index}]");
        }
    }

    private void ValidatePolicy(List<ConfigDiagnostic> diagnostics)
    {
        var hijack = new RouteRulePlan(RoutePhase.DnsHijack)
        {
            Mode = LogicalMatchMode.And,
            Rules =
            [
                new(RoutePhase.DnsHijack) { Inbound = [SingboxTags.TunInbound] },
                new(RoutePhase.DnsHijack)
                {
                    Mode = LogicalMatchMode.Or,
                    Rules = [new(RoutePhase.DnsHijack) { Protocol = ["dns"] }, new(RoutePhase.DnsHijack) { Port = [53] }]
                }
            ],
            Action = RouteOperation.HijackDns
        };
        if (Rules.IsEmpty || !Rules[0].HasSamePolicy(hijack))
            diagnostics.Add(new("IR011", "plan.route.rules", "路由必须首先劫持 TUN 的 DNS 协议及端口 53 流量。"));
        foreach (RoutePhase phase in new[] { RoutePhase.EarlyIpv6Guard, RoutePhase.LateIpv6Guard })
        {
            var rules = Rules.Where(rule => rule.Phase == phase).ToArray();
            var expected = new RouteRulePlan(phase) { IpVersion = 6, Action = RouteOperation.Reject };
            if (AddressFamily == AddressFamilyPolicy.Auto)
                expected = expected with
                {
                    IpVersion = null,
                    Mode = LogicalMatchMode.And,
                    Rules = [new(phase) { IpVersion = 6 }, new(phase) { DefaultInterfaceAddress = [AddressFamilyPolicies.GlobalIpv6Prefix], Invert = true }]
                };
            bool valid = AddressFamily == AddressFamilyPolicy.DualStack
                ? rules.Length == 0 : rules.Length == 1 && rules[0].HasSamePolicy(expected);
            if (!valid)
                diagnostics.Add(new("IR011", "plan.route.rules", "实际路由必须包含符合地址族策略的早晚守卫。"));
        }
        foreach (RoutePhase phase in new[] { RoutePhase.PreSniffDirect, RoutePhase.Sniff, RoutePhase.BlockStunAndAds, RoutePhase.DomesticUdp443, RoutePhase.DomesticDirect })
            if (!Rules.Any(rule => rule.Phase == phase))
                diagnostics.Add(new("IR011", "plan.route.rules", "必要路由阶段必须包含实际规则。"));
        if (TailscaleEnabled && !Rules.Any(rule => rule.Phase == RoutePhase.Tailscale && rule.HasSamePolicy(
            new(RoutePhase.Tailscale)
            {
                Inbound = [SingboxTags.TunInbound],
                PreferredBy = [SingboxTags.TailscaleEndpoint],
                Action = RouteOperation.Route,
                Outbound = SingboxTags.TailscaleEndpoint
            })))
            diagnostics.Add(new("IR011", "plan.route.rules", "Tailscale 控制面路由缺失或不符合策略。"));
    }
}

internal enum RoutePhase
{
    DnsHijack,
    EarlyIpv6Guard,
    Tailscale,
    PreSniffDirect,
    Sniff,
    BlockStunAndAds,
    LateIpv6Guard,
    PriorityServices,
    DomesticUdp443,
    OtherServices,
    DomesticDirect,
    FinalOutbound
}

internal sealed record RuleSetPlan(ImmutableArray<string> Tags, string Url, string UpdateInterval);

/// <summary>An ordered policy decision; the emitter only maps its fields to the target model.</summary>
internal sealed record RouteRulePlan(RoutePhase Phase)
{
    public LogicalMatchMode? Mode { get; init; }
    public ImmutableArray<RouteRulePlan> Rules { get; init; }
    public ImmutableArray<string> Inbound { get; init; }
    public int? IpVersion { get; init; }
    public ImmutableArray<string> Protocol { get; init; }
    public ImmutableArray<int> Port { get; init; }
    public ImmutableArray<string> Network { get; init; }
    public RouteOperation? Action { get; init; }
    public ImmutableArray<string> RuleSet { get; init; }
    public ImmutableArray<string> IpCidr { get; init; }
    public bool? IpIsPrivate { get; init; }
    public ImmutableArray<string> PreferredBy { get; init; }
    public ImmutableArray<string> DefaultInterfaceAddress { get; init; }
    public bool? Invert { get; init; }
    public string? Outbound { get; init; }
    public ImmutableArray<string> Sniffer { get; init; }
    public string? Timeout { get; init; }

    internal bool HasSamePolicy(RouteRulePlan other) =>
        (this with { Rules = default, Inbound = default, Protocol = default, Port = default, Network = default, RuleSet = default, IpCidr = default, PreferredBy = default, DefaultInterfaceAddress = default, Sniffer = default })
        == (other with { Rules = default, Inbound = default, Protocol = default, Port = default, Network = default, RuleSet = default, IpCidr = default, PreferredBy = default, DefaultInterfaceAddress = default, Sniffer = default })
        && Same(Inbound, other.Inbound) && Same(Protocol, other.Protocol) && Same(Port, other.Port)
        && Same(Network, other.Network) && Same(RuleSet, other.RuleSet) && Same(IpCidr, other.IpCidr)
        && Same(PreferredBy, other.PreferredBy) && Same(DefaultInterfaceAddress, other.DefaultInterfaceAddress) && Same(Sniffer, other.Sniffer)
        && Rules.IsDefault == other.Rules.IsDefault
        && (Rules.IsDefault || Rules.Length == other.Rules.Length && Rules.Zip(other.Rules).All(pair => pair.First.HasSamePolicy(pair.Second)));

    private static bool Same<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefault == right.IsDefault && (left.IsDefault || left.SequenceEqual(right));
}

internal enum LogicalMatchMode { And, Or }
internal enum RouteOperation { HijackDns, Route, Reject, Sniff, Bypass }
