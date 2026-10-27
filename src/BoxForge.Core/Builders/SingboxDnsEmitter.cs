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
            Server = resolver.Address,
            Detour = resolver.Detour,
            Tls = new DnsTlsConfig { Enabled = true, ServerName = resolver.ServerName }
        }));
        if (plan.TailscaleEnabled)
        {
            dns.Servers.Add(new TailscaleDnsServer
            {
                Tag = SingboxTags.TailscaleDns,
                Endpoint = SingboxTags.TailscaleEndpoint,
                AcceptDefaultResolvers = false
            });
        }
        EmitRules(dns.Rules, plan);
        return dns;
    }

    private static void EmitRules(List<DnsRule> rules, DnsPlan plan)
    {
        if (plan.AddressFamily == AddressFamilyPolicy.Ipv4Only)
            rules.Add(new DnsRule
            {
                QueryType = ["AAAA"],
                Action = DnsRuleAction.Predefined,
                Rcode = DnsResponseCode.NoError
            });
        else if (plan.AddressFamily.UsesInterfaceGuard())
            rules.Add(new DnsRule
            {
                Type = RouteRuleType.Logical,
                Mode = RouteLogicalMode.And,
                Rules =
                [
                    new DnsRule { QueryType = ["AAAA"] },
                    new DnsRule { DefaultInterfaceAddress = [AddressFamilyPolicies.GlobalIpv6Prefix], Invert = true }
                ],
                Action = DnsRuleAction.Predefined,
                Rcode = DnsResponseCode.NoError
            });
        if (plan.TailscaleEnabled)
            rules.Add(new DnsRule
            {
                PreferredBy = [SingboxTags.TailscaleDns],
                Action = DnsRuleAction.Route,
                Server = SingboxTags.TailscaleDns,
                DisableOptimisticCache = true
            });
        rules.Add(new DnsRule
        {
            RuleSet = [RuleSetTags.Ads],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.NameError
        });
        foreach (DnsResolverPoolPlan pool in plan.Pools)
        {
            switch (plan.SelectionMode)
            {
                case DnsResolverSelectionMode.SequentialFallback:
                    AddEvaluate(rules, pool, primary: true, timeout: "2s");
                    AddResponse(rules, pool, primary: true, DnsResponseCode.NoError);
                    AddResponse(rules, pool, primary: true, DnsResponseCode.NameError);
                    AddEvaluate(rules, pool, primary: false);
                    AddResponse(rules, pool, primary: false, DnsResponseCode.NoError);
                    AddResponse(rules, pool, primary: false, DnsResponseCode.NameError);
                    break;
                case DnsResolverSelectionMode.ParallelFastest:
                    foreach (string queryType in plan.AddressFamily == AddressFamilyPolicy.Ipv4Only
                        ? new[] { "A" } : new[] { "A", "AAAA" })
                    {
                        AddEvaluate(rules, pool, primary: true, queryType);
                        AddEvaluate(rules, pool, primary: false, queryType);
                        AddResponse(rules, pool, primary: true, DnsResponseCode.NoError, queryType, race: true);
                        AddResponse(rules, pool, primary: false, DnsResponseCode.NoError, queryType, race: true);
                        // Empty answers and negative responses follow both address races.
                        AddResponse(rules, pool, primary: true, DnsResponseCode.NoError, queryType);
                        AddResponse(rules, pool, primary: false, DnsResponseCode.NoError, queryType);
                        AddResponse(rules, pool, primary: true, DnsResponseCode.NameError, queryType);
                        AddResponse(rules, pool, primary: false, DnsResponseCode.NameError, queryType);
                        AddTerminalServfail(rules, pool, queryType);
                    }
                    // Scoped address terminals prevent repeated queries in the general pool.
                    AddEvaluate(rules, pool, primary: true);
                    AddEvaluate(rules, pool, primary: false);
                    AddResponse(rules, pool, primary: true, DnsResponseCode.NoError, race: true);
                    AddResponse(rules, pool, primary: false, DnsResponseCode.NoError, race: true);
                    AddResponse(rules, pool, primary: true, DnsResponseCode.NameError);
                    AddResponse(rules, pool, primary: false, DnsResponseCode.NameError);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported DNS selection mode.");
            }
            AddTerminalServfail(rules, pool);
        }
    }

    private static void AddTerminalServfail(List<DnsRule> rules, DnsResolverPoolPlan pool, string? queryType = null) =>
        rules.Add(new DnsRule
        {
            RuleSet = Copy(pool.RuleSets),
            QueryType = queryType is null ? null : [queryType],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.ServerFailure
        });

    private static void AddEvaluate(List<DnsRule> rules, DnsResolverPoolPlan pool, bool primary,
        string? queryType = null, string? timeout = null) =>
        rules.Add(new DnsRule
        {
            RuleSet = Copy(pool.RuleSets),
            QueryType = queryType is null ? null : [queryType],
            DisableOptimisticCache = queryType is not null ? true : null,
            Action = DnsRuleAction.Evaluate,
            Server = primary ? pool.PrimaryServer : pool.SecondaryServer,
            Tag = ResponseTag(pool, primary, queryType),
            Timeout = timeout
        });

    private static void AddResponse(List<DnsRule> rules, DnsResolverPoolPlan pool, bool primary,
        DnsResponseCode rcode, string? queryType = null, bool race = false) =>
        rules.Add(new DnsRule
        {
            // Domain rule-sets and IP predicates share an OR group in sing-box.
            // The evaluated-response tag already carries the pool's domain scope.
            RuleSet = race && queryType is not null ? null : Copy(pool.RuleSets),
            QueryType = queryType is null ? null : [queryType],
            IpCidr = race && queryType is not null ? [queryType == "A" ? "0.0.0.0/0" : "::/0"] : null,
            MatchResponse = ResponseTag(pool, primary, queryType),
            ResponseRcode = rcode,
            Action = DnsRuleAction.Respond,
            Race = race ? true : null
        });

    private static string ResponseTag(DnsResolverPoolPlan pool, bool primary, string? queryType)
    {
        string name = pool.Kind switch
        {
            DnsPoolKind.Service => $"service-{pool.ServiceId}",
            DnsPoolKind.Domestic => "domestic",
            DnsPoolKind.Global => "global",
            _ => throw new InvalidOperationException("Unsupported DNS pool kind.")
        };
        string tag = $"response-{name}-{(primary ? "primary" : "secondary")}";
        return queryType is null ? tag : DnsResponseTags.ForAddressQuery(tag, queryType);
    }

    private static List<string>? Copy(ImmutableArray<string> values) => values.IsDefault ? null : [.. values];
}
