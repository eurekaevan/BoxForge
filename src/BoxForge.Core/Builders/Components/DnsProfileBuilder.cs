using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed class DnsProfileBuilder(IOptions<TailscaleOptions> tailscaleOptions)
{
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public DnsConfig Build(TargetPlatform platform)
    {
        AddressFamilyPolicy addressFamily = AddressFamilyPolicies.For(platform);
        DnsResolverSelectionMode selectionMode =
            DnsResolverSelectionPolicies.For(platform);
        var dns = new DnsConfig { Strategy = addressFamily.ToDnsStrategy() };

        dns.Servers.AddRange([
            CreateHttpsServer(SingboxTags.DirectAliDns, "223.5.5.5", "dns.alidns.com"),
            CreateHttpsServer(SingboxTags.DirectTencentDns, "119.29.29.29", "doh.pub"),
            CreateHttpsServer(SingboxTags.ProxyCloudflareDns, "1.1.1.1", "cloudflare-dns.com", SingboxTags.MainProxyGroup),
            CreateHttpsServer(SingboxTags.ProxyGoogleDns, "8.8.8.8", "dns.google", SingboxTags.MainProxyGroup)
        ]);

        if (addressFamily == AddressFamilyPolicy.Ipv4Only)
        {
            dns.Rules.Add(new DnsRule
            {
                QueryType = ["AAAA"],
                Action = DnsRuleAction.Predefined,
                Rcode = DnsResponseCode.NoError
            });
        }

        if (tailscale.IsEnabled(platform))
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
            RuleSet = [RuleSetTags.Ads],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.NameError
        });

        List<string> priorityRuleSets =
        [
            .. ProfileDefinitions.Services
                .Where(service => service.PrecedesDomesticRoutes)
                .SelectMany(service => service.RuleSets)
                .Distinct(StringComparer.Ordinal)
        ];
        if (priorityRuleSets.Count > 0)
        {
            CompilePool(dns.Rules, new ResolverPoolDefinition(
                priorityRuleSets, SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns,
                DnsResponseTags.PriorityPrimary, DnsResponseTags.PrioritySecondary),
                selectionMode);
        }

        CompilePool(dns.Rules, new ResolverPoolDefinition(
            [RuleSetTags.Cn], SingboxTags.DirectAliDns, SingboxTags.DirectTencentDns,
            DnsResponseTags.DomesticPrimary, DnsResponseTags.DomesticSecondary),
            selectionMode);
        CompilePool(dns.Rules, new ResolverPoolDefinition(
            null, SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns,
            DnsResponseTags.GlobalPrimary, DnsResponseTags.GlobalSecondary),
            selectionMode);
        return dns;
    }

    private static HttpsDnsServer CreateHttpsServer(
        string tag,
        string server,
        string serverName,
        string? detour = null) => new()
        {
            Tag = tag,
            ServerAddress = server,
            DetourTag = detour,
            TlsConfig = new DnsTlsConfig { ServerName = serverName }
        };

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
            case DnsResolverSelectionMode.ParallelPrimaryPreferred:
                CompileParallelPrimaryPreferred(rules, pool);
                break;
            case DnsResolverSelectionMode.ParallelFastest:
                CompileParallelFastest(rules, pool);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(selectionMode), selectionMode, null);
        }

        rules.Add(new DnsRule
        {
            RuleSet = pool.RuleSets,
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.ServerFailure
        });
    }

    private static void CompileSequentialFallback(
        List<DnsRule> rules,
        ResolverPoolDefinition pool)
    {
        AddEvaluate(rules, pool, primary: true, timeout: "2s");
        AddAcceptedResponses(rules, pool.RuleSets, pool.PrimaryResponse);
        AddEvaluate(rules, pool, primary: false);
        AddAcceptedResponses(rules, pool.RuleSets, pool.SecondaryResponse);
    }

    private static void CompileParallelPrimaryPreferred(
        List<DnsRule> rules,
        ResolverPoolDefinition pool)
    {
        AddEvaluate(rules, pool, primary: true);
        AddEvaluate(rules, pool, primary: false);
        AddAcceptedResponses(rules, pool.RuleSets, pool.PrimaryResponse);
        AddAcceptedResponses(rules, pool.RuleSets, pool.SecondaryResponse);
    }

    private static void CompileParallelFastest(
        List<DnsRule> rules,
        ResolverPoolDefinition pool)
    {
        AddEvaluate(rules, pool, primary: true);
        AddEvaluate(rules, pool, primary: false);
        AddResponse(rules, pool.RuleSets, pool.PrimaryResponse,
            DnsResponseCode.NoError, race: true);
        AddResponse(rules, pool.RuleSets, pool.SecondaryResponse,
            DnsResponseCode.NoError, race: true);
        AddResponse(rules, pool.RuleSets, pool.PrimaryResponse,
            DnsResponseCode.NameError);
        AddResponse(rules, pool.RuleSets, pool.SecondaryResponse,
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
            Action = DnsRuleAction.Evaluate,
            Server = primary ? pool.PrimaryServer : pool.SecondaryServer,
            Tag = primary ? pool.PrimaryResponse : pool.SecondaryResponse,
            Timeout = timeout
        });
    }

    private static void AddAcceptedResponses(
        List<DnsRule> rules,
        List<string>? ruleSets,
        string responseTag)
    {
        foreach (DnsResponseCode rcode in new[]
                 { DnsResponseCode.NoError, DnsResponseCode.NameError })
        {
            AddResponse(rules, ruleSets, responseTag, rcode);
        }
    }

    private static void AddResponse(
        List<DnsRule> rules,
        List<string>? ruleSets,
        string responseTag,
        DnsResponseCode rcode,
        bool race = false) =>
        rules.Add(new DnsRule
        {
            RuleSet = ruleSets,
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
        string SecondaryResponse);
}
