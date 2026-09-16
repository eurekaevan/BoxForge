using System.Text.RegularExpressions;
using System.Collections.Immutable;
using BoxForge.Models;

namespace BoxForge.Configuration;

public static partial class ProfileDefinitions
{
    public static ImmutableArray<RegionDefinition> Regions { get; } =
    [
        new(RegionId.UnitedStates, "🇺🇸 US", UnitedStatesPattern()),
        new(RegionId.Japan, "🇯🇵 JP", JapanPattern()),
        new(RegionId.HongKong, "🇭🇰 HK", HongKongPattern()),
        new(RegionId.Singapore, "🇸🇬 SG", SingaporePattern()),
    ];

    public static ImmutableArray<ServiceDefinition> Services { get; } =
    [
        new(
            ServiceGroupNames.Ai,
            RegionId.UnitedStates,
            [RuleSetTags.Ai],
            PrecedesDomesticRoutes: true),
        new(
            ServiceGroupNames.Google,
            RegionId.UnitedStates,
            [RuleSetTags.Google],
            PrecedesDomesticRoutes: true),
        new(ServiceGroupNames.Spotify, RegionId.UnitedStates, [RuleSetTags.Spotify]),
        new(ServiceGroupNames.Games, RegionId.HongKong, [RuleSetTags.Games]),
        new(ServiceGroupNames.Microsoft, RegionId.UnitedStates, [RuleSetTags.Microsoft])
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
