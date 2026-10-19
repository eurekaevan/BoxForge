using BoxForge.Configuration;

namespace BoxForge.Models.Intermediate;

internal sealed record RoutePlan(
    AddressFamilyPolicy AddressFamily,
    bool TailscaleEnabled,
    bool UseDirectForwarding,
    string DirectResolverAddress,
    string SniffTimeout,
    string FinalOutbound,
    IReadOnlyList<BuiltInProfile.RuleSetSource> RuleSets,
    IReadOnlyList<ServiceDefinition> PriorityServices,
    IReadOnlyList<ServiceDefinition> OtherServices,
    IReadOnlyList<RoutePhase> Phases)
{
    internal void Validate()
    {
        if (!Phases.SequenceEqual(Enum.GetValues<RoutePhase>()))
            throw new InvalidOperationException("路由阶段必须完整、唯一并符合声明顺序。");
        if (!Enum.IsDefined(AddressFamily))
            throw new InvalidOperationException("路由计划的地址族无效。");
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (BuiltInProfile.RuleSetSource source in RuleSets)
        {
            if (source.Tags.IsDefaultOrEmpty || source.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || !tags.Add(tag)))
                throw new InvalidOperationException("路由规则集标签必须非空且唯一。");
        }
        if (!tags.IsSupersetOf([RuleSetTags.Ads, RuleSetTags.Cn, RuleSetTags.CnIp]))
            throw new InvalidOperationException("路由计划缺少广告或国内规则集。");
        var services = new HashSet<string>(StringComparer.Ordinal);
        foreach (ServiceDefinition service in PriorityServices.Concat(OtherServices))
        {
            if (string.IsNullOrWhiteSpace(service.Name) || !services.Add(service.Name)
                || service.RuleSets.IsDefaultOrEmpty || service.RuleSets.Any(tag => !tags.Contains(tag)))
                throw new InvalidOperationException("路由服务必须唯一且引用已声明的规则集。");
        }
        if (PriorityServices.Any(service => !service.PrecedesDomesticRoutes)
            || OtherServices.Any(service => service.PrecedesDomesticRoutes))
            throw new InvalidOperationException("路由服务分类必须遵循国内路由优先级声明。");
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
    PriorityUdp443Reject,
    LateIpv6Guard,
    PriorityServices,
    DomesticUdp443,
    OtherServices,
    DomesticDirect,
    FinalOutbound
}
