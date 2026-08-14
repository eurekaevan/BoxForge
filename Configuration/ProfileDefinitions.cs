using System.Text.RegularExpressions;
using System.Collections.Immutable;
using BoxForge.Models;

namespace BoxForge.Configuration;

public static partial class ProfileDefinitions
{
    public static ImmutableArray<RegionDefinition> Regions { get; } =
    [
        new(RegionId.UnitedStates, "🇺🇸 美国", UnitedStatesPattern()),
        new(RegionId.Japan, "🇯🇵 日本", JapanPattern()),
        new(RegionId.HongKong, "🇭🇰 香港", HongKongPattern()),
        new(RegionId.Singapore, "🇸🇬 狮城", SingaporePattern()),
    ];

    public static ImmutableArray<ServiceDefinition> Services { get; } =
    [
        new(
            ServiceGroupNames.Google,
            RegionId.UnitedStates,
            ["geosite-google"],
            PrecedesDomesticRoutes: true),
        new(ServiceGroupNames.Spotify, RegionId.UnitedStates, ["geosite-spotify"]),
        new(ServiceGroupNames.Steam, RegionId.HongKong, ["geosite-steam"]),
        new(ServiceGroupNames.Ai, RegionId.UnitedStates, ["geosite-category-ai-!cn"]),
        new(ServiceGroupNames.Microsoft, RegionId.UnitedStates, ["geosite-microsoft"])
    ];

    [GeneratedRegex(
        @"香港|hong\s?kong|深港|🇭🇰|(?<![a-zA-Z])hkg?\d*(?![a-zA-Z])",
        RegexOptions.IgnoreCase)]
    private static partial Regex HongKongPattern();

    [GeneratedRegex(
        @"狮城|新加坡|singapore|🇸🇬|(?<![a-zA-Z])sgp?\d*(?![a-zA-Z])",
        RegexOptions.IgnoreCase)]
    private static partial Regex SingaporePattern();

    [GeneratedRegex(
        @"日本|japan|tokyo|东京|大阪|🇯🇵|(?<![a-zA-Z])jpn?\d*(?![a-zA-Z])",
        RegexOptions.IgnoreCase)]
    private static partial Regex JapanPattern();

    [GeneratedRegex(
        @"美国|america|洛杉矶|硅谷|🇺🇸|(?<![a-zA-Z])usa?\d*(?![a-zA-Z])",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnitedStatesPattern();
}
