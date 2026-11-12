using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoxForge.Models;

namespace BoxForge.Configuration;

public sealed record FeaturesProfile(TailscaleFeature Tailscale, ApiFeature Api);
public sealed record TailscaleFeature(bool Enabled, string TaildropDirectory);
public sealed record ApiFeature(bool Enabled);
public sealed record PrivacyProfile;

internal sealed record DnsProfile(
    AddressFamilyPolicy AddressFamily,
    DnsAddressMode AddressMode,
    [property: JsonPropertyName("fakeip")] FakeIpProfile FakeIp,
    DnsResolverSelectionMode SelectionMode,
    string Timeout,
    uint CacheCapacity,
    ImmutableArray<BuiltInProfile.Resolver> Resolvers);

internal sealed record FakeIpProfile(
    string Inet4Range,
    string Inet6Range,
    [property: JsonPropertyName("realip_domains")] ImmutableArray<string> RealIpDomains,
    [property: JsonPropertyName("realip_domain_suffixes")] ImmutableArray<string> RealIpDomainSuffixes);

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
    bool? MultiQueue);

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
