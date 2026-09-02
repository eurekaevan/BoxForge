using System.Text.Json.Serialization;

namespace BoxForge.Models.Singbox;

[JsonConverter(typeof(JsonStringEnumConverter<DnsRuleAction>))]
public enum DnsRuleAction
{
    [JsonStringEnumMemberName("route")] Route,
    [JsonStringEnumMemberName("evaluate")] Evaluate,
    [JsonStringEnumMemberName("respond")] Respond,
    [JsonStringEnumMemberName("predefined")] Predefined,
    [JsonStringEnumMemberName("reject")] Reject,
    [JsonStringEnumMemberName("route-options")] RouteOptions
}

[JsonConverter(typeof(JsonStringEnumConverter<RouteRuleAction>))]
public enum RouteRuleAction
{
    [JsonStringEnumMemberName("route")] Route,
    [JsonStringEnumMemberName("reject")] Reject,
    [JsonStringEnumMemberName("sniff")] Sniff,
    [JsonStringEnumMemberName("resolve")] Resolve,
    [JsonStringEnumMemberName("hijack-dns")] HijackDns
}

[JsonConverter(typeof(JsonStringEnumConverter<RouteRuleType>))]
public enum RouteRuleType
{
    [JsonStringEnumMemberName("logical")] Logical
}

[JsonConverter(typeof(JsonStringEnumConverter<RouteLogicalMode>))]
public enum RouteLogicalMode
{
    [JsonStringEnumMemberName("and")] And,
    [JsonStringEnumMemberName("or")] Or
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleSetType>))]
public enum RuleSetType
{
    [JsonStringEnumMemberName("remote")] Remote
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleSetFormat>))]
public enum RuleSetFormat
{
    [JsonStringEnumMemberName("binary")] Binary
}

[JsonConverter(typeof(JsonStringEnumConverter<DnsResponseCode>))]
public enum DnsResponseCode
{
    [JsonStringEnumMemberName("NOERROR")] NoError,
    [JsonStringEnumMemberName("FORMERR")] FormatError,
    [JsonStringEnumMemberName("SERVFAIL")] ServerFailure,
    [JsonStringEnumMemberName("NXDOMAIN")] NameError,
    [JsonStringEnumMemberName("NOTIMP")] NotImplemented,
    [JsonStringEnumMemberName("REFUSED")] Refused
}

[JsonConverter(typeof(JsonStringEnumConverter<DnsStrategy>))]
public enum DnsStrategy
{
    [JsonStringEnumMemberName("prefer_ipv4")] PreferIpv4,
    [JsonStringEnumMemberName("ipv4_only")] Ipv4Only
}
