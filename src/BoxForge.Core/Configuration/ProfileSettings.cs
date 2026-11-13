using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoxForge.Models;

namespace BoxForge.Configuration;

public sealed record FeaturesProfile(TailscaleFeature Tailscale, ApiFeature Api);
public sealed record TailscaleFeature(bool Enabled, string TaildropDirectory);
public sealed record ApiFeature(bool Enabled);
public sealed record PrivacyProfile;

public sealed record DnsProfile(
    AddressFamilyPolicy AddressFamily,
    DnsAddressMode AddressMode,
    [property: JsonPropertyName("fakeip")] FakeIpProfile FakeIp,
    DnsResolverSelectionMode SelectionMode,
    string Timeout,
    uint CacheCapacity,
    ImmutableArray<DnsResolverProfile> Resolvers);

public sealed record FakeIpProfile(
    string Inet4Range,
    string Inet6Range,
    [property: JsonPropertyName("realip_domains")] ImmutableArray<string> RealIpDomains,
    [property: JsonPropertyName("realip_domain_suffixes")] ImmutableArray<string> RealIpDomainSuffixes);

public sealed record RouteProfile(
    ImmutableArray<RouteRegionProfile> Regions,
    ImmutableArray<ServiceDefinition> Services,
    ImmutableArray<RouteRuleSetProfile> RuleSets,
    string MainDefaultRegion,
    string SniffTimeout,
    ImmutableArray<int> SniffExcludedPorts,
    DomainBlockProfile Block,
    ImmutableArray<string> TunAddresses,
    bool? StrictRoute,
    bool? AutoRedirect,
    bool? MultiQueue);

public sealed record DomainBlockProfile(
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

public sealed record DnsResolverProfile(string Id, string Server, string ServerName);
public sealed record RouteRegionProfile(string Id, string DisplayName, string Pattern);
public sealed record RouteRuleSetProfile(ImmutableArray<string> Tags, string Url, string UpdateInterval);
