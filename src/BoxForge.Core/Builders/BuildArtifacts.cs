using BoxForge.Models.Intermediate;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Configuration;

namespace BoxForge.Builders;

internal sealed record SingboxBuildRequest(
    NodeCatalog Nodes,
    TargetPlatform Platform,
    string? CacheId)
{
    public BoxForgeProfile? Profile { get; init; }
}

internal sealed record NodeCatalog(IReadOnlyList<ProxyNode> Nodes, IReadOnlyList<string> Names);

internal sealed record ProfilePlan(
    SelectorOutbound MainOutbound,
    IReadOnlyList<SelectorOutbound> RegionOutbounds,
    IReadOnlyList<UrlTestOutbound> RegionAutoOutbounds,
    IReadOnlyList<SelectorOutbound> ServiceOutbounds,
    DirectOutbound DirectOutbound
);
