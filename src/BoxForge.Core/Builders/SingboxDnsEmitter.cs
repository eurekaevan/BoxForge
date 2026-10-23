using System.Collections.Immutable;
using BoxForge.Configuration;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxDnsEmitter
{
    internal static DnsConfig Emit(DnsPlan plan)
    {
        var dns = new DnsConfig
        {
            Timeout = plan.Timeout,
            CacheCapacity = plan.CacheCapacity,
            Optimistic = new DnsOptimisticConfig { Enabled = true, Timeout = plan.OptimisticTimeout },
            ReverseMapping = true,
            Strategy = plan.AddressFamily.ToDnsStrategy(),
            Final = plan.FinalResolver
        };
        dns.Servers.AddRange(plan.Resolvers.Select(resolver => new HttpsDnsServer
        {
            Tag = resolver.Name,
            ServerAddress = resolver.Address,
            DetourTag = resolver.Detour,
            TlsConfig = new DnsTlsConfig { Enabled = true, ServerName = resolver.ServerName }
        }));
        if (plan.TailscaleEnabled)
        {
            dns.Servers.Add(new TailscaleDnsServer
            {
                Tag = SingboxTags.TailscaleDns,
                EndpointTag = SingboxTags.TailscaleEndpoint,
                AcceptDefaultResolversValue = false
            });
        }
        dns.Rules.AddRange(plan.Rules.Select(EmitRule));
        return dns;
    }

    private static DnsRule EmitRule(DnsRulePlan rule) => new()
    {
        Type = rule.Mode is null ? null : RouteRuleType.Logical,
        Mode = rule.Mode switch
        {
            null => null,
            LogicalMatchMode.And => RouteLogicalMode.And,
            LogicalMatchMode.Or => RouteLogicalMode.Or,
            _ => throw new InvalidOperationException("Unsupported DNS logical mode.")
        },
        Rules = rule.Rules.IsDefault ? null : rule.Rules.Select(EmitRule).ToList(),
        Invert = rule.Invert,
        DefaultInterfaceAddress = Copy(rule.DefaultInterfaceAddress),
        Timeout = rule.Timeout,
        RuleSet = Copy(rule.RuleSet),
        QueryType = Copy(rule.QueryType),
        PreferredBy = Copy(rule.PreferredBy),
        IpCidr = Copy(rule.IpCidr),
        MatchResponse = rule.MatchResponse,
        ResponseRcode = EmitResultCode(rule.ResponseRcode),
        Action = rule.Action switch
        {
            null => null,
            DnsOperation.Predefined => DnsRuleAction.Predefined,
            DnsOperation.Route => DnsRuleAction.Route,
            DnsOperation.Evaluate => DnsRuleAction.Evaluate,
            DnsOperation.Respond => DnsRuleAction.Respond,
            _ => throw new InvalidOperationException("Unsupported DNS operation.")
        },
        Server = rule.Server,
        Tag = rule.Tag,
        Race = rule.Race,
        DisableOptimisticCache = rule.DisableOptimisticCache,
        Rcode = EmitResultCode(rule.Rcode)
    };

    private static List<string>? Copy(ImmutableArray<string> values) => values.IsDefault ? null : [.. values];

    private static DnsResponseCode? EmitResultCode(DnsResultCode? code) => code switch
    {
        null => null,
        DnsResultCode.NoError => DnsResponseCode.NoError,
        DnsResultCode.NameError => DnsResponseCode.NameError,
        DnsResultCode.ServerFailure => DnsResponseCode.ServerFailure,
        _ => throw new InvalidOperationException("Unsupported DNS response code.")
    };
}
