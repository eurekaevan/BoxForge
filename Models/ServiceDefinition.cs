namespace BoxForge.Models;

using System.Text.RegularExpressions;
using System.Collections.Immutable;

public record ServiceDefinition(
    string Name,
    RegionId? DefaultRegion,
    ImmutableArray<string> RuleSets,
    bool PrecedesDomesticRoutes = false
);

public record RegionDefinition(
    RegionId Id,
    string DisplayName,
    Regex Pattern);
