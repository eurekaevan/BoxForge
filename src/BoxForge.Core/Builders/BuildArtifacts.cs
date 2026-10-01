using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Configuration;

namespace BoxForge.Builders;

public sealed record SingboxBuildRequest(
    NodeCatalog Nodes,
    TargetPlatform Platform,
    string? CacheId)
{
    public AddressFamilyPolicy AddressFamily { get; init; } = AddressFamilyPolicies.For(Platform);
}

public sealed record NodeCatalog(
    IReadOnlyList<ProxyOutbound> Outbounds,
    IReadOnlyList<string> Names
);

public sealed record ProfilePlan(
    SelectorOutbound MainOutbound,
    IReadOnlyList<SelectorOutbound> RegionOutbounds,
    IReadOnlyList<UrlTestOutbound> RegionAutoOutbounds,
    IReadOnlyList<SelectorOutbound> ServiceOutbounds,
    DirectOutbound DirectOutbound
);
