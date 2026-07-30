using SubConvert.Models;
using SubConvert.Models.Clash;
using SubConvert.Models.Singbox;

namespace SubConvert.Builders;

public sealed record SingboxBuildRequest(
    ClashConfig ClashConfig,
    TargetPlatform Platform,
    string CacheId,
    bool StrictNodeValidation = false
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
