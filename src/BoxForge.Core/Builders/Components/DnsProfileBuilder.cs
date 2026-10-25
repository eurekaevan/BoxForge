using System.Collections.Immutable;
using BoxForge.Configuration;
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
        var pools = new List<ResolverPoolDefinition>();
        foreach (var service in settingsProfile.Services.OrderByDescending(service => service.PrecedesDomesticRoutes))
        {
            string primary = SingboxTags.ServiceDns(service.Id, "cloudflare");
            string secondary = SingboxTags.ServiceDns(service.Id, "google");
            resolvers.Add(CreateResolver(primary, settingsProfile.GetResolver("cloudflare"), service.Name));
            resolvers.Add(CreateResolver(secondary, settingsProfile.GetResolver("google"), service.Name));
            pools.Add(new ResolverPoolDefinition($"service-{service.Id}", service.RuleSets, primary, secondary));
        }
        pools.Add(new ResolverPoolDefinition("domestic", [RuleSetTags.Cn],
            SingboxTags.DirectAliDns, SingboxTags.DirectTencentDns));
        pools.Add(new ResolverPoolDefinition("global", default,
            SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns));
        bool tailscaleEnabled = settingsProfile.Features.Tailscale.Enabled;
        return new DnsPlan(settings.AddressFamily, settings.DnsSelectionMode,
            resolvers, tailscaleEnabled,
            SingboxTags.ProxyCloudflareDns,
            settingsProfile.Dns.Timeout, settingsProfile.Dns.CacheCapacity,
            settingsProfile.Dns.OptimisticTimeout)
        {
            Rules = PlanRules(settings.AddressFamily, tailscaleEnabled, pools, settings.DnsSelectionMode)
        };
    }

    private static ImmutableArray<DnsRulePlan> PlanRules(
        AddressFamilyPolicy addressFamily,
        bool tailscaleEnabled,
        IReadOnlyList<ResolverPoolDefinition> pools,
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
            RuleSet = [RuleSetTags.Ads],
            Action = DnsOperation.Predefined,
            Rcode = DnsResultCode.NameError
        });
        foreach (ResolverPoolDefinition pool in pools)
            CompilePool(rules, pool, selectionMode, addressFamily != AddressFamilyPolicy.Ipv4Only);
        return [.. rules];
    }

    private static DnsHttpsResolverPlan CreateResolver(
        string name, BuiltInProfile.Resolver resolver, string? detour = null) =>
        new(name, resolver.Server, resolver.ServerName, detour);

    private static void CompilePool(
        List<DnsRulePlan> rules,
        ResolverPoolDefinition pool,
        DnsResolverSelectionMode selectionMode,
        bool includeIpv6)
    {
        switch (selectionMode)
        {
            case DnsResolverSelectionMode.SequentialFallback:
                CompileSequentialFallback(rules, pool);
                break;
            case DnsResolverSelectionMode.ParallelFastest:
                CompileParallelFastest(rules, pool, includeIpv6);
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
        ResolverPoolDefinition pool,
        bool includeIpv6)
    {
        foreach (string queryType in includeIpv6 ? new[] { "A", "AAAA" } : new[] { "A" })
        {
            var addressPool = pool with
            {
                QueryType = queryType
            };
            AddEvaluate(rules, addressPool, primary: true);
            AddEvaluate(rules, addressPool, primary: false);
            string addressCidr = queryType == "A" ? "0.0.0.0/0" : "::/0";
            AddResponse(rules, addressPool, addressPool.PrimaryResponse,
                DnsResultCode.NoError, race: true, addressCidr: addressCidr);
            AddResponse(rules, addressPool, addressPool.SecondaryResponse,
                DnsResultCode.NoError, race: true, addressCidr: addressCidr);
            // Empty answers and negative responses are fallbacks after both address races.
            AddResponse(rules, addressPool, addressPool.PrimaryResponse, DnsResultCode.NoError);
            AddResponse(rules, addressPool, addressPool.SecondaryResponse, DnsResultCode.NoError);
            AddResponse(rules, addressPool, addressPool.PrimaryResponse, DnsResultCode.NameError);
            AddResponse(rules, addressPool, addressPool.SecondaryResponse, DnsResultCode.NameError);
            AddTerminalServfail(rules, addressPool);
        }

        // Scoped address terminals prevent repeated queries in the general pool.
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
            DisableOptimisticCache = pool.QueryType is "A" or "AAAA" ? true : null,
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
        string? addressCidr = null) =>
        rules.Add(new DnsRulePlan
        {
            // Domain rule-sets and IP predicates share an OR group in sing-box.
            // The unique evaluated-response tag already carries the pool's domain scope.
            RuleSet = addressCidr is not null ? default : pool.RuleSets,
            QueryType = pool.QueryType is null ? default : [pool.QueryType],
            IpCidr = addressCidr is not null ? [addressCidr] : default,
            MatchResponse = responseTag,
            ResponseRcode = rcode,
            Action = DnsOperation.Respond,
            Race = race ? true : null
        });

    private sealed record ResolverPoolDefinition(
        string Name,
        ImmutableArray<string> RuleSets,
        string PrimaryServer,
        string SecondaryServer,
        string? QueryType = null)
    {
        internal string PrimaryResponse => ResponseTag("primary");
        internal string SecondaryResponse => ResponseTag("secondary");

        private string ResponseTag(string priority)
        {
            string tag = $"response-{Name}-{priority}";
            return QueryType is null ? tag : DnsResponseTags.ForAddressQuery(tag, QueryType);
        }
    }
}
