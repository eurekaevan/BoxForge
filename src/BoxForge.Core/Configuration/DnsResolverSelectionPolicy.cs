using BoxForge.Models;

namespace BoxForge.Configuration;

public enum DnsResolverSelectionMode
{
    SequentialFallback,
    ParallelFastest = 2
}

public static class DnsResolverSelectionPolicies
{
    public static DnsResolverSelectionMode For(TargetPlatform platform) =>
        PlatformProfile.For(platform).DnsSelectionMode;
}
