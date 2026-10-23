using BoxForge.Configuration;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxRouteEmitter
{
    internal static RouteConfig Emit(RoutePlan plan)
    {
        var route = new RouteConfig
        {
            Final = plan.FinalOutbound,
            DefaultHttpClient = plan.DefaultHttpClient,
            DefaultDomainResolver = SingboxResolverEmitter.Emit(plan.DefaultDomainResolver)
        };
        route.RuleSet.AddRange(plan.RuleSets.Select(source => new SingboxRuleSet
        {
            Tag = [.. source.Tags],
            Type = RuleSetType.Remote,
            Format = RuleSetFormat.Binary,
            Url = source.Url,
            UpdateInterval = source.UpdateInterval
        }));
        route.Rules.AddRange(plan.Rules.Select(EmitRule));
        return route;
    }

    private static RouteRule EmitRule(RouteRulePlan rule) => new()
    {
        Type = rule.Mode is null ? null : RouteRuleType.Logical,
        Mode = rule.Mode switch
        {
            null => null,
            LogicalMatchMode.And => RouteLogicalMode.And,
            LogicalMatchMode.Or => RouteLogicalMode.Or,
            _ => throw new InvalidOperationException("无效的逻辑匹配。")
        },
        Rules = rule.Rules.IsDefault ? null : rule.Rules.Select(EmitRule).ToList(),
        Inbound = Values(rule.Inbound),
        IpVersion = rule.IpVersion,
        Protocol = Values(rule.Protocol),
        Port = Values(rule.Port),
        Network = Values(rule.Network),
        Action = rule.Action switch
        {
            null => null,
            RouteOperation.HijackDns => RouteRuleAction.HijackDns,
            RouteOperation.Route => RouteRuleAction.Route,
            RouteOperation.Reject => RouteRuleAction.Reject,
            RouteOperation.Sniff => RouteRuleAction.Sniff,
            RouteOperation.Bypass => RouteRuleAction.Bypass,
            _ => throw new InvalidOperationException("无效的路由动作。")
        },
        RuleSet = Values(rule.RuleSet),
        IpCidr = Values(rule.IpCidr),
        IpIsPrivate = rule.IpIsPrivate,
        PreferredBy = Values(rule.PreferredBy),
        DefaultInterfaceAddress = Values(rule.DefaultInterfaceAddress),
        Invert = rule.Invert,
        Outbound = rule.Outbound,
        Sniffer = Values(rule.Sniffer),
        Timeout = rule.Timeout
    };

    private static List<T>? Values<T>(System.Collections.Immutable.ImmutableArray<T> values) =>
        values.IsDefault ? null : values.ToList();
}
