using System.Collections.Immutable;

namespace BoxForge.Configuration;

public sealed record RuntimeProfile(TailscaleRuntimeProfile Tailscale, SingboxApiRuntimeProfile SingboxApi);

public sealed record TailscaleRuntimeProfile(bool Enabled, bool AndroidEnabled)
{
    public bool IsEnabled(PlatformProfile platform) => platform.IsMobile ? AndroidEnabled : Enabled;
}

public sealed record SingboxApiRuntimeProfile(bool Enabled);

public sealed record ProfilePolicies(
    string MainDefaultRegion,
    string SniffTimeout,
    ImmutableArray<int> SniffExcludedPorts,
    string UrlTestUrl,
    int UrlTestTolerance,
    string UrlTestIdleTimeout,
    string DnsTimeout,
    uint DnsCacheCapacity,
    string DnsOptimisticTimeout);
