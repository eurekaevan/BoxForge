using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

public sealed record SingboxBuildRequest(
    NodeCatalog Nodes,
    TargetPlatform Platform,
    string? CacheId
);

public sealed record NodeCatalog(
    IReadOnlyList<ProxyOutbound> Outbounds,
    IReadOnlyList<string> Names,
    IReadOnlyList<string> ServerDomains
);

public sealed record ProfilePlan(
    SelectorOutbound MainOutbound,
    IReadOnlyList<SelectorOutbound> RegionOutbounds,
    IReadOnlyList<SelectorOutbound> ServiceOutbounds,
    DirectOutbound DirectOutbound
);
