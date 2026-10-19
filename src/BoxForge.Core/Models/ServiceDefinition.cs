namespace BoxForge.Models;

using System.Text.RegularExpressions;
using System.Collections.Immutable;

public record ServiceDefinition(
    string Id,
    string Name,
    string? DefaultRegion,
    ImmutableArray<string> RuleSets,
    bool PrecedesDomesticRoutes = false
);

public record RegionDefinition(
    string Id,
    string DisplayName,
    Regex Pattern);
