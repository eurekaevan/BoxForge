using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BoxForge.Models;

namespace BoxForge.Configuration;

public sealed record FeaturesProfile(TailscaleFeature Tailscale, ApiFeature Api);
public sealed record TailscaleFeature(bool Enabled, string TaildropDirectory);
public sealed record ApiFeature(bool Enabled);
public sealed record PrivacyProfile;

internal sealed record DnsProfile(
    AddressFamilyPolicy AddressFamily,
    DnsResolverSelectionMode SelectionMode,
    string Timeout,
    uint CacheCapacity,
    string OptimisticTimeout,
    ImmutableArray<BuiltInProfile.Resolver> Resolvers);

internal sealed record RouteProfile(
    ImmutableArray<BuiltInProfile.Region> Regions,
    ImmutableArray<ServiceDefinition> Services,
    ImmutableArray<BuiltInProfile.RuleSetSource> RuleSets,
    string MainDefaultRegion,
    string SniffTimeout,
    ImmutableArray<int> SniffExcludedPorts,
    DomainBlockProfile Block,
    ImmutableArray<string> TunAddresses,
    bool? StrictRoute,
    bool? AutoRedirect,
    bool? MultiQueue,
    bool UseDirectForwarding);

internal sealed record DomainBlockProfile(
    ImmutableArray<string> Domain,
    ImmutableArray<string> DomainSuffix,
    ImmutableArray<string> DomainWildcard)
{
    internal bool IsEmpty => Domain.IsEmpty && DomainSuffix.IsEmpty && DomainWildcard.IsEmpty;

    internal List<string>? RegexPatterns() => DomainWildcard.IsEmpty ? null : [.. DomainWildcard.Select(pattern =>
        "(?i)^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$")];
}

public sealed record TuningProfile(
    string ConnectTimeout,
    string? TcpKeepAlive,
    string? TcpKeepAliveInterval,
    string UrlTestInterval,
    string UrlTestUrl,
    int UrlTestTolerance,
    string UrlTestIdleTimeout);
