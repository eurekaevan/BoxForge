using BoxForge.Models;

namespace BoxForge.Configuration;

public enum DnsResolverSelectionMode
{
    SequentialFallback,
    ParallelPrimaryPreferred,
    ParallelFastest
}

public static class DnsResolverSelectionPolicies
{
    public static DnsResolverSelectionMode For(TargetPlatform platform) =>
        PlatformProfile.For(platform).DnsSelectionMode;
}
