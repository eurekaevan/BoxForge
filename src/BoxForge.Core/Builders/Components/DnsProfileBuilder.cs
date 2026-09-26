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
                DnsResponseTags.PriorityPrimary, DnsResponseTags.PrioritySecondary));
        }

        CompilePool(dns.Rules, new ResolverPoolDefinition(
            [RuleSetTags.Cn], SingboxTags.DirectAliDns, SingboxTags.DirectTencentDns,
            DnsResponseTags.DomesticPrimary, DnsResponseTags.DomesticSecondary));
        CompilePool(dns.Rules, new ResolverPoolDefinition(
            null, SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns,
            DnsResponseTags.GlobalPrimary, DnsResponseTags.GlobalSecondary));
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

    private static void CompilePool(List<DnsRule> rules, ResolverPoolDefinition pool)
    {
        // Both queries start before response matching. Matching remains ordered:
        // a healthy primary wins even when the secondary finishes first.
        rules.Add(new DnsRule
        {
            RuleSet = pool.RuleSets,
            Action = DnsRuleAction.Evaluate,
            Server = pool.PrimaryServer,
            Tag = pool.PrimaryResponse
        });
        rules.Add(new DnsRule
        {
            RuleSet = pool.RuleSets,
            Action = DnsRuleAction.Evaluate,
            Server = pool.SecondaryServer,
            Tag = pool.SecondaryResponse
        });
        AddAcceptedResponses(rules, pool.RuleSets, pool.PrimaryResponse);
        AddAcceptedResponses(rules, pool.RuleSets, pool.SecondaryResponse);

        // This scope boundary handles transport errors and unwanted rcodes.
        rules.Add(new DnsRule
        {
            RuleSet = pool.RuleSets,
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.ServerFailure
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
            rules.Add(new DnsRule
            {
                RuleSet = ruleSets,
                MatchResponse = responseTag,
                ResponseRcode = rcode,
                Action = DnsRuleAction.Respond
            });
        }
    }

    private sealed record ResolverPoolDefinition(
        List<string>? RuleSets,
        string PrimaryServer,
        string SecondaryServer,
        string PrimaryResponse,
        string SecondaryResponse);
}
