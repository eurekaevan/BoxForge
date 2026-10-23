using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal sealed record NodeCatalog(IReadOnlyList<ProxyNode> Nodes, IReadOnlyList<string> Names);

internal sealed record ProfilePlan(
    SelectorOutbound MainOutbound,
    IReadOnlyList<SelectorOutbound> RegionOutbounds,
    IReadOnlyList<UrlTestOutbound> RegionAutoOutbounds,
    IReadOnlyList<SelectorOutbound> ServiceOutbounds,
    DirectOutbound DirectOutbound
);
