using System.Collections.Immutable;
using BoxForge.Configuration;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxDnsEmitter
{
    private static readonly ImmutableArray<string> LocalSuffixes =
        ["home.arpa", "internal", "lan", "local", "localdomain", "localhost"];
    internal static DnsConfig Emit(DnsPlan plan)
    {
        var dns = new DnsConfig
        {
            Timeout = plan.Timeout,
            CacheCapacity = plan.CacheCapacity,
            Optimistic = new DnsOptimisticConfig { Enabled = false },
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
        dns.Servers.Add(new LocalDnsServer { Tag = SingboxTags.LocalDns });
        if (plan.AddressMode == DnsAddressMode.FakeIp)
        {
            FakeIpPlan fakeIp = plan.FakeIp
                ?? throw new InvalidOperationException("FakeIp DNS mode requires a FakeIP plan.");
            dns.Servers.Add(new FakeIpDnsServer
            {
                Tag = SingboxTags.FakeIpDns,
                Inet4Range = fakeIp.Inet4Range,
                Inet6Range = plan.AddressFamily == AddressFamilyPolicy.DualStack
                    ? fakeIp.Inet6Range
                    : null
            });
        }
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
        if (plan.FakeIp is { } exceptions)
            plan = plan with
            {
                FakeIp = exceptions with
                {
                    RealIpDomains = [.. exceptions.RealIpDomains.Where(name => !IsLocalName(name))],
                    RealIpDomainSuffixes = [.. exceptions.RealIpDomainSuffixes.Where(name => !IsLocalName(name))]
                }
            };
        if (plan.AddressFamily == AddressFamilyPolicy.Ipv4Only)
            rules.Add(new DnsRule
            {
                QueryType = ["AAAA"],
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
        if (!plan.Block.IsEmpty)
            rules.Add(new DnsRule
            {
                Domain = plan.Block.Domain.IsEmpty ? null : [.. plan.Block.Domain.Select(domain => domain.ToLowerInvariant())],
                DomainSuffix = plan.Block.DomainSuffix.IsEmpty ? null : [.. plan.Block.DomainSuffix.Select(domain => domain.ToLowerInvariant())],
                DomainRegex = plan.Block.RegexPatterns(),
                Action = DnsRuleAction.Predefined,
                Rcode = DnsResponseCode.NameError
            });
        rules.Add(new DnsRule
        {
            DomainSuffix = [.. LocalSuffixes],
            Action = DnsRuleAction.Route,
            Server = SingboxTags.LocalDns
        });
        foreach (DnsResolverPoolPlan pool in plan.Pools)
        {
            bool fakeIp = plan.AddressMode == DnsAddressMode.FakeIp && pool.Kind != DnsPoolKind.Domestic;
            if (fakeIp)
                AddFakeIpRule(rules, pool, plan);
            bool hasRealAddresses = !fakeIp || plan.FakeIp!.RealIpDomains.Length > 0
                || plan.FakeIp.RealIpDomainSuffixes.Length > 0;
            bool parallel = plan.SelectionMode == DnsResolverSelectionMode.ParallelFastest;
            if (hasRealAddresses)
                foreach (string queryType in plan.AddressFamily == AddressFamilyPolicy.Ipv4Only
                    ? new[] { "A" } : new[] { "A", "AAAA" })
                {
                    AddEvaluate(rules, pool, primary: true, queryType, timeout: parallel ? null : "2s");
                    if (!parallel)
                        AddResponse(rules, pool, primary: true, DnsResponseCode.NoError, queryType, requireAddress: true);
                    AddEvaluate(rules, pool, primary: false, queryType);
                    if (parallel)
                        AddResponse(rules, pool, primary: true, DnsResponseCode.NoError, queryType, race: true);
                    AddResponse(rules, pool, primary: false, DnsResponseCode.NoError, queryType,
                        race: parallel, requireAddress: true);
                    // Prefer actual addresses before accepting empty or negative responses.
                    AddResponse(rules, pool, primary: true, DnsResponseCode.NoError, queryType);
                    AddResponse(rules, pool, primary: false, DnsResponseCode.NoError, queryType);
                    AddResponse(rules, pool, primary: true, DnsResponseCode.NameError, queryType);
                    AddResponse(rules, pool, primary: false, DnsResponseCode.NameError, queryType);
                    AddTerminalServfail(rules, pool, queryType);
                }
            // Non-address queries preserve their original primary/fallback or race policy.
            AddEvaluate(rules, pool, primary: true, timeout: parallel ? null : "2s");
            if (!parallel)
            {
                AddResponse(rules, pool, primary: true, DnsResponseCode.NoError);
                AddResponse(rules, pool, primary: true, DnsResponseCode.NameError);
            }
            AddEvaluate(rules, pool, primary: false);
            if (parallel)
                AddResponse(rules, pool, primary: true, DnsResponseCode.NoError, race: true);
            AddResponse(rules, pool, primary: false, DnsResponseCode.NoError, race: parallel);
            if (parallel)
                AddResponse(rules, pool, primary: true, DnsResponseCode.NameError);
            AddResponse(rules, pool, primary: false, DnsResponseCode.NameError);
            // Catch non-address queries and act as the final per-pool terminal.
            AddTerminalServfail(rules, pool);
        }
    }

    private static void AddFakeIpRule(List<DnsRule> rules, DnsResolverPoolPlan pool, DnsPlan plan)
    {
        FakeIpPlan fakeIp = plan.FakeIp
            ?? throw new InvalidOperationException("FakeIp DNS mode requires a FakeIP plan.");
        var conditions = new List<DnsRule>();
        if (!pool.RuleSets.IsDefault)
            conditions.Add(new DnsRule { RuleSet = Copy(pool.RuleSets) });
        conditions.Add(new DnsRule
        {
            QueryType = plan.AddressFamily == AddressFamilyPolicy.Ipv4Only ? ["A"] : ["A", "AAAA"]
        });
        if (!fakeIp.RealIpDomains.IsDefaultOrEmpty || !fakeIp.RealIpDomainSuffixes.IsDefaultOrEmpty)
            conditions.Add(new DnsRule
            {
                Invert = true,
                Domain = fakeIp.RealIpDomains.IsDefaultOrEmpty ? null : [.. fakeIp.RealIpDomains],
                DomainSuffix = fakeIp.RealIpDomainSuffixes.IsDefaultOrEmpty ? null : [.. fakeIp.RealIpDomainSuffixes]
            });
        rules.Add((conditions.Count == 1 ? conditions[0] : new DnsRule
        {
            Type = RouteRuleType.Logical,
            Mode = RouteLogicalMode.And,
            Rules = conditions
        }) with
        {
            Action = DnsRuleAction.Route,
            Server = SingboxTags.FakeIpDns
        });
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
            Action = DnsRuleAction.Evaluate,
            Server = primary ? pool.PrimaryServer : pool.SecondaryServer,
            Tag = ResponseTag(pool, primary, queryType),
            Timeout = timeout
        });

    private static void AddResponse(List<DnsRule> rules, DnsResolverPoolPlan pool, bool primary,
        DnsResponseCode rcode, string? queryType = null, bool race = false, bool requireAddress = false) =>
        rules.Add(new DnsRule
        {
            // The unique response tag already binds this rule to the evaluate scope.
            QueryType = queryType is null ? null : [queryType],
            IpCidr = (race || requireAddress) && queryType is not null ? [queryType == "A" ? "0.0.0.0/0" : "::/0"] : null,
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

    private static bool IsLocalName(string name) =>
        LocalSuffixes.Any(suffix => name.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));

    private static List<string>? Copy(ImmutableArray<string> values) => values.IsDefault ? null : [.. values];
}
