using System.Text.RegularExpressions;
using BoxForge.Models;

namespace BoxForge.Configuration;

public static class ProfileDefinitions
{
    public static readonly IReadOnlyDictionary<RegionId, (string DisplayName, Regex Pattern)> Regions = new Dictionary<RegionId, (string, Regex)>
    {
        { RegionId.HongKong, ("🇭🇰 香港", new Regex(@"(?i)香港|hong\s?kong|深港|🇭🇰|(?<![a-zA-Z])hkg?\d*(?![a-zA-Z])", RegexOptions.Compiled)) },
        { RegionId.Singapore, ("🇸🇬 狮城", new Regex(@"(?i)狮城|新加坡|singapore|🇸🇬|(?<![a-zA-Z])sgp?\d*(?![a-zA-Z])", RegexOptions.Compiled)) },
        { RegionId.Japan, ("🇯🇵 日本", new Regex(@"(?i)日本|japan|tokyo|东京|大阪|🇯🇵|(?<![a-zA-Z])jpn?\d*(?![a-zA-Z])", RegexOptions.Compiled)) },
        { RegionId.UnitedStates, ("🇺🇸 美国", new Regex(@"(?i)美国|america|洛杉矶|硅谷|🇺🇸|(?<![a-zA-Z])usa?\d*(?![a-zA-Z])", RegexOptions.Compiled)) },
    };

    public static readonly IReadOnlyList<ServiceDefinition> Services =
    [
        new(ServiceGroupNames.Spotify, RegionId.UnitedStates, ["geosite-spotify"]),
        new(ServiceGroupNames.Steam, RegionId.HongKong, ["geosite-steam"]),
        new(ServiceGroupNames.Ai, RegionId.UnitedStates, ["geosite-category-ai-!cn"]),
        new(ServiceGroupNames.Microsoft, RegionId.UnitedStates, ["geosite-microsoft"]),
    ];
}
