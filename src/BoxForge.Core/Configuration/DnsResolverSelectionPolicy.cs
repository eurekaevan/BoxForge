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
    public static DnsResolverSelectionMode For(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Android => DnsResolverSelectionMode.SequentialFallback,
        TargetPlatform.Linux or TargetPlatform.Windows =>
            DnsResolverSelectionMode.ParallelFastest,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
    };
}
