using System.Collections.Immutable;
using BoxForge.Models;

namespace BoxForge.Configuration;

public static class ProfileDefinitions
{
    public static ImmutableArray<RegionDefinition> Regions => BoxForgeProfile.Default.Regions;

    public static ImmutableArray<ServiceDefinition> Services => BoxForgeProfile.Default.Services;
}
