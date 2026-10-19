using BoxForge.Configuration;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxDnsEmitter
{
    private const string SequentialPrimaryTimeout = "2s";

    internal static DnsConfig Emit(DnsPlan plan)
    {
        plan.Validate();
        var dns = new DnsConfig
        {
            Strategy = plan.AddressFamily.ToDnsStrategy(),
            Final = plan.FinalResolver
        };
        dns.Servers.AddRange(plan.Resolvers.Select(resolver => new HttpsDnsServer
        {
            Tag = resolver.Name,
            ServerAddress = resolver.Address,
            DetourTag = resolver.Detour,
            TlsConfig = new DnsTlsConfig { ServerName = resolver.ServerName }
        }));
        if (plan.AddressFamily == AddressFamilyPolicy.Ipv4Only)
        {
            dns.Rules.Add(new DnsRule
            {
                QueryType = ["AAAA"],
                Action = DnsRuleAction.Predefined,
                Rcode = DnsResponseCode.NoError
            });
        }
        if (plan.TailscaleEnabled)
        {
            dns.Servers.Add(new TailscaleDnsServer
            {
                Tag = SingboxTags.TailscaleDns,
                EndpointTag = SingboxTags.TailscaleEndpoint,
                AcceptDefaultResolversValue = false
            });
            dns.Rules.Add(new DnsRule
            {
                PreferredBy = [SingboxTags.TailscaleDns],
                Action = DnsRuleAction.Route,
                Server = SingboxTags.TailscaleDns,
                DisableOptimisticCache = true
            });
        }
        dns.Rules.Add(new DnsRule
        {
            RuleSet = [.. plan.BlockedRuleSets],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.NameError
        });
        foreach (DnsResolverPoolPlan pool in plan.Pools)
        {
            CompilePool(dns.Rules, new ResolverPoolDefinition(
                pool.RuleSets?.ToList(), pool.PrimaryResolver, pool.SecondaryResolver,
                $"response-{pool.Name}-primary", $"response-{pool.Name}-secondary"), plan.SelectionMode);
        }
        return dns;
    }

    private static void CompilePool(
        List<DnsRule> rules,
        ResolverPoolDefinition pool,
        DnsResolverSelectionMode selectionMode)
    {
        switch (selectionMode)
        {
            case DnsResolverSelectionMode.SequentialFallback:
                CompileSequentialFallback(rules, pool);
                break;
            case DnsResolverSelectionMode.ParallelFastest:
                CompileParallelFastest(rules, pool);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(selectionMode), selectionMode, null);
        }

        AddTerminalServfail(rules, pool);
    }

    private static void AddTerminalServfail(List<DnsRule> rules, ResolverPoolDefinition pool) =>
        rules.Add(new DnsRule
        {
            RuleSet = pool.RuleSets,
            QueryType = pool.QueryType is null ? null : [pool.QueryType],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.ServerFailure
        });

    private static void CompileSequentialFallback(
        List<DnsRule> rules,
        ResolverPoolDefinition pool)
    {
        AddEvaluate(rules, pool, primary: true, timeout: SequentialPrimaryTimeout);
        AddAcceptedResponses(rules, pool, pool.PrimaryResponse);
        AddEvaluate(rules, pool, primary: false);
        AddAcceptedResponses(rules, pool, pool.SecondaryResponse);
    }

    private static void CompileParallelFastest(
        List<DnsRule> rules,
        ResolverPoolDefinition pool)
    {
        var addressPool = pool with
        {
            QueryType = "A",
            PrimaryResponse = DnsResponseTags.ForAddressQuery(pool.PrimaryResponse),
            SecondaryResponse = DnsResponseTags.ForAddressQuery(pool.SecondaryResponse)
        };
        AddEvaluate(rules, addressPool, primary: true);
        AddEvaluate(rules, addressPool, primary: false);
        AddResponse(rules, addressPool, addressPool.PrimaryResponse,
            DnsResponseCode.NoError, race: true, requireIpv4: true);
        AddResponse(rules, addressPool, addressPool.SecondaryResponse,
            DnsResponseCode.NoError, race: true, requireIpv4: true);
        // Empty answers and negative responses are fallbacks after both address races.
        AddResponse(rules, addressPool, addressPool.PrimaryResponse, DnsResponseCode.NoError);
        AddResponse(rules, addressPool, addressPool.SecondaryResponse, DnsResponseCode.NoError);
        AddResponse(rules, addressPool, addressPool.PrimaryResponse, DnsResponseCode.NameError);
        AddResponse(rules, addressPool, addressPool.SecondaryResponse, DnsResponseCode.NameError);
        AddTerminalServfail(rules, addressPool);

        // The scoped A terminal prevents A queries from entering this unchanged general pool.
        AddEvaluate(rules, pool, primary: true);
        AddEvaluate(rules, pool, primary: false);
        AddResponse(rules, pool, pool.PrimaryResponse,
            DnsResponseCode.NoError, race: true);
        AddResponse(rules, pool, pool.SecondaryResponse,
            DnsResponseCode.NoError, race: true);
        AddResponse(rules, pool, pool.PrimaryResponse,
            DnsResponseCode.NameError);
        AddResponse(rules, pool, pool.SecondaryResponse,
            DnsResponseCode.NameError);
    }

    private static void AddEvaluate(
        List<DnsRule> rules,
        ResolverPoolDefinition pool,
        bool primary,
        string? timeout = null)
    {
        rules.Add(new DnsRule
        {
            RuleSet = pool.RuleSets,
            QueryType = pool.QueryType is null ? null : [pool.QueryType],
            DisableOptimisticCache = pool.QueryType == "A" ? true : null,
            Action = DnsRuleAction.Evaluate,
            Server = primary ? pool.PrimaryServer : pool.SecondaryServer,
            Tag = primary ? pool.PrimaryResponse : pool.SecondaryResponse,
            Timeout = timeout
        });
    }

    private static void AddAcceptedResponses(
        List<DnsRule> rules,
        ResolverPoolDefinition pool,
        string responseTag)
    {
        foreach (DnsResponseCode rcode in new[]
                 { DnsResponseCode.NoError, DnsResponseCode.NameError })
        {
            AddResponse(rules, pool, responseTag, rcode);
        }
    }

    private static void AddResponse(
        List<DnsRule> rules,
        ResolverPoolDefinition pool,
        string responseTag,
        DnsResponseCode rcode,
        bool race = false,
        bool requireIpv4 = false) =>
        rules.Add(new DnsRule
        {
            // Domain rule-sets and IP predicates share an OR group in sing-box.
            // The unique evaluated-response tag already carries the pool's domain scope.
            RuleSet = requireIpv4 ? null : pool.RuleSets,
            QueryType = pool.QueryType is null ? null : [pool.QueryType],
            IpCidr = requireIpv4 ? ["0.0.0.0/0"] : null,
            MatchResponse = responseTag,
            ResponseRcode = rcode,
            Action = DnsRuleAction.Respond,
            Race = race ? true : null
        });

    private sealed record ResolverPoolDefinition(
        List<string>? RuleSets,
        string PrimaryServer,
        string SecondaryServer,
        string PrimaryResponse,
        string SecondaryResponse,
        string? QueryType = null);
}
