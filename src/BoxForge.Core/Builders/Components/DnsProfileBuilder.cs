using System.Collections.Immutable;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal static class DnsProfileBuilder
{
    internal static DnsPlan Plan(ResolvedProfile settingsProfile)
    {
        PlatformProfile settings = settingsProfile.Settings;
        List<DnsHttpsResolverPlan> resolvers =
        [
            CreateResolver(SingboxTags.DirectAliDns, settingsProfile.GetResolver("alidns")),
            CreateResolver(SingboxTags.DirectTencentDns, settingsProfile.GetResolver("tencent")),
            CreateResolver(SingboxTags.ProxyCloudflareDns, settingsProfile.GetResolver("cloudflare"), SingboxTags.MainProxyGroup),
            CreateResolver(SingboxTags.ProxyGoogleDns, settingsProfile.GetResolver("google"), SingboxTags.MainProxyGroup)
        ];
        var pools = new List<DnsResolverPoolPlan>();
        foreach (var service in settingsProfile.Services.OrderByDescending(service => service.PrecedesDomesticRoutes))
        {
            string primary = SingboxTags.ServiceDns(service.Id, "cloudflare");
            string secondary = SingboxTags.ServiceDns(service.Id, "google");
            resolvers.Add(CreateResolver(primary, settingsProfile.GetResolver("cloudflare"), service.Name));
            resolvers.Add(CreateResolver(secondary, settingsProfile.GetResolver("google"), service.Name));
            pools.Add(new DnsResolverPoolPlan($"service-{service.Id}", service.RuleSets, primary, secondary));
        }
        pools.Add(new DnsResolverPoolPlan("domestic", [RuleSetTags.Cn],
            SingboxTags.DirectAliDns, SingboxTags.DirectTencentDns));
        pools.Add(new DnsResolverPoolPlan("global", null,
            SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns));
        string[] blockedRuleSets = [RuleSetTags.Ads];
        bool tailscaleEnabled = settingsProfile.Features.Tailscale.Enabled;
        return new DnsPlan(settings.AddressFamily, settings.DnsSelectionMode,
            resolvers, tailscaleEnabled,
            blockedRuleSets, pools,
            SingboxTags.ProxyCloudflareDns,
            settingsProfile.Dns.Timeout, settingsProfile.Dns.CacheCapacity,
            settingsProfile.Dns.OptimisticTimeout)
        {
            Rules = PlanRules(settings.AddressFamily, tailscaleEnabled, blockedRuleSets, pools, settings.DnsSelectionMode)
        };
    }

    private static ImmutableArray<DnsRulePlan> PlanRules(
        AddressFamilyPolicy addressFamily,
        bool tailscaleEnabled,
        IReadOnlyList<string> blockedRuleSets,
        IReadOnlyList<DnsResolverPoolPlan> pools,
        DnsResolverSelectionMode selectionMode)
    {
        var rules = new List<DnsRulePlan>();
        if (addressFamily == AddressFamilyPolicy.Ipv4Only)
        {
            rules.Add(new DnsRulePlan
            {
                QueryType = ["AAAA"],
                Action = DnsOperation.Predefined,
                Rcode = DnsResultCode.NoError
            });
        }
        else if (addressFamily.UsesInterfaceGuard())
        {
            rules.Add(new DnsRulePlan
            {
                Mode = LogicalMatchMode.And,
                Rules =
                [
                    new DnsRulePlan { QueryType = ["AAAA"] },
                    new DnsRulePlan { DefaultInterfaceAddress = [AddressFamilyPolicies.GlobalIpv6Prefix], Invert = true }
                ],
                Action = DnsOperation.Predefined,
                Rcode = DnsResultCode.NoError
            });
        }
        if (tailscaleEnabled)
        {
            rules.Add(new DnsRulePlan
            {
                PreferredBy = [SingboxTags.TailscaleDns],
                Action = DnsOperation.Route,
                Server = SingboxTags.TailscaleDns,
                DisableOptimisticCache = true
            });
        }
        rules.Add(new DnsRulePlan
        {
            RuleSet = [.. blockedRuleSets],
            Action = DnsOperation.Predefined,
            Rcode = DnsResultCode.NameError
        });
        foreach (DnsResolverPoolPlan pool in pools)
        {
            CompilePool(rules, new ResolverPoolDefinition(
                pool.RuleSets is null ? default : [.. pool.RuleSets], pool.PrimaryResolver, pool.SecondaryResolver,
                $"response-{pool.Name}-primary", $"response-{pool.Name}-secondary"), selectionMode);
        }
        return [.. rules];
    }

    private static DnsHttpsResolverPlan CreateResolver(
        string name, BuiltInProfile.Resolver resolver, string? detour = null) =>
        new(name, resolver.Server, resolver.ServerName, detour);

    private static void CompilePool(
        List<DnsRulePlan> rules,
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

    private static void AddTerminalServfail(List<DnsRulePlan> rules, ResolverPoolDefinition pool) =>
        rules.Add(new DnsRulePlan
        {
            RuleSet = pool.RuleSets,
            QueryType = pool.QueryType is null ? default : [pool.QueryType],
            Action = DnsOperation.Predefined,
            Rcode = DnsResultCode.ServerFailure
        });

    private static void CompileSequentialFallback(
        List<DnsRulePlan> rules,
        ResolverPoolDefinition pool)
    {
        AddEvaluate(rules, pool, primary: true, timeout: DnsPlan.SequentialPrimaryTimeout);
        AddAcceptedResponses(rules, pool, pool.PrimaryResponse);
        AddEvaluate(rules, pool, primary: false);
        AddAcceptedResponses(rules, pool, pool.SecondaryResponse);
    }

    private static void CompileParallelFastest(
        List<DnsRulePlan> rules,
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
            DnsResultCode.NoError, race: true, requireIpv4: true);
        AddResponse(rules, addressPool, addressPool.SecondaryResponse,
            DnsResultCode.NoError, race: true, requireIpv4: true);
        // Empty answers and negative responses are fallbacks after both address races.
        AddResponse(rules, addressPool, addressPool.PrimaryResponse, DnsResultCode.NoError);
        AddResponse(rules, addressPool, addressPool.SecondaryResponse, DnsResultCode.NoError);
        AddResponse(rules, addressPool, addressPool.PrimaryResponse, DnsResultCode.NameError);
        AddResponse(rules, addressPool, addressPool.SecondaryResponse, DnsResultCode.NameError);
        AddTerminalServfail(rules, addressPool);

        // The scoped A terminal prevents A queries from entering this unchanged general pool.
        AddEvaluate(rules, pool, primary: true);
        AddEvaluate(rules, pool, primary: false);
        AddResponse(rules, pool, pool.PrimaryResponse,
            DnsResultCode.NoError, race: true);
        AddResponse(rules, pool, pool.SecondaryResponse,
            DnsResultCode.NoError, race: true);
        AddResponse(rules, pool, pool.PrimaryResponse,
            DnsResultCode.NameError);
        AddResponse(rules, pool, pool.SecondaryResponse,
            DnsResultCode.NameError);
    }

    private static void AddEvaluate(
        List<DnsRulePlan> rules,
        ResolverPoolDefinition pool,
        bool primary,
        string? timeout = null)
    {
        rules.Add(new DnsRulePlan
        {
            RuleSet = pool.RuleSets,
            QueryType = pool.QueryType is null ? default : [pool.QueryType],
            DisableOptimisticCache = pool.QueryType == "A" ? true : null,
            Action = DnsOperation.Evaluate,
            Server = primary ? pool.PrimaryServer : pool.SecondaryServer,
            Tag = primary ? pool.PrimaryResponse : pool.SecondaryResponse,
            Timeout = timeout
        });
    }

    private static void AddAcceptedResponses(
        List<DnsRulePlan> rules,
        ResolverPoolDefinition pool,
        string responseTag)
    {
        foreach (DnsResultCode rcode in new[]
                 { DnsResultCode.NoError, DnsResultCode.NameError })
        {
            AddResponse(rules, pool, responseTag, rcode);
        }
    }

    private static void AddResponse(
        List<DnsRulePlan> rules,
        ResolverPoolDefinition pool,
        string responseTag,
        DnsResultCode rcode,
        bool race = false,
        bool requireIpv4 = false) =>
        rules.Add(new DnsRulePlan
        {
            // Domain rule-sets and IP predicates share an OR group in sing-box.
            // The unique evaluated-response tag already carries the pool's domain scope.
            RuleSet = requireIpv4 ? default : pool.RuleSets,
            QueryType = pool.QueryType is null ? default : [pool.QueryType],
            IpCidr = requireIpv4 ? ["0.0.0.0/0"] : default,
            MatchResponse = responseTag,
            ResponseRcode = rcode,
            Action = DnsOperation.Respond,
            Race = race ? true : null
        });

    private sealed record ResolverPoolDefinition(
        ImmutableArray<string> RuleSets,
        string PrimaryServer,
        string SecondaryServer,
        string PrimaryResponse,
        string SecondaryResponse,
        string? QueryType = null);
}
