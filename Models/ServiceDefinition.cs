namespace BoxForge.Models;

public record ServiceDefinition(
    string Name,              // 服务名称 (如 "🎵 Spotify")
    RegionId? DefaultRegion,  // 默认节点地区 (如 RegionId.UnitedStates)
    List<string> RuleSets     // 绑定的路由规则集 (如 ["geosite-spotify"])
);