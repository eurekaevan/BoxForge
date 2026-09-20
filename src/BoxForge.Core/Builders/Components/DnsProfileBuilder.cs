using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed class DnsProfileBuilder(
    IOptions<TailscaleOptions> tailscaleOptions)
{
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public DnsConfig Build(NodeCatalog nodes, TargetPlatform platform)
    {
        var dns = new DnsConfig();

        // Keep every DNS client on IPv4, including Tailscale split DNS and
        // callers that explicitly request AAAA records.
        dns.Rules.Add(new DnsRule
        {
            QueryType = ["AAAA"],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.NoError
        });

        dns.Servers.AddRange([
            CreateHttpsServer(SingboxTags.NodeResolverDns, "223.5.5.5", "dns.alidns.com"),
            CreateHttpsServer(SingboxTags.LocalDns, "223.5.5.5", "dns.alidns.com"),
            CreateHttpsServer(SingboxTags.LocalTencentDns, "119.29.29.29", "doh.pub"),
            CreateHttpsServer(
                SingboxTags.RemoteGoogleDns,
                "8.8.8.8",
                "dns.google",
                SingboxTags.MainProxyGroup),
            CreateHttpsServer(
                SingboxTags.RemoteDns,
                "1.1.1.1",
                "cloudflare-dns.com",
                SingboxTags.MainProxyGroup)
        ]);

        if (tailscale.IsEnabled(platform))
        {
            dns.Servers.Insert(
                0,
                CreateHttpsServer(
                    SingboxTags.BootstrapDns,
                    "223.5.5.5",
                    "dns.alidns.com"));
            dns.Servers.Add(new TailscaleDnsServer
            {
                Tag = SingboxTags.TailscaleDns,
                EndpointTag = SingboxTags.TailscaleEndpoint,
                AcceptDefaultResolversValue = false
            });

            // sing-box 直接根据 Tailscale 的 MagicDNS 域名与分流后缀匹配。
            dns.Rules.Add(new DnsRule
            {
                PreferredBy = [SingboxTags.TailscaleDns],
                Action = DnsRuleAction.Route,
                Server = SingboxTags.TailscaleDns,
                DisableOptimisticCache = true
            });
        }

        if (nodes.ServerDomains.Count > 0)
        {
            dns.Rules.Add(new DnsRule
            {
                Domain = [.. nodes.ServerDomains],
                QueryType = ["A"],
                Action = DnsRuleAction.Route,
                Server = SingboxTags.NodeResolverDns,
                DisableOptimisticCache = true
            });
        }

        dns.Rules.Add(new DnsRule
        {
            RuleSet =
            [
                RuleSetTags.Ads
            ],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.NameError
        });

        AddPrimaryFallback(
            dns.Rules,
            [RuleSetTags.Google],
            SingboxTags.RemoteDns,
            SingboxTags.RemoteGoogleDns,
            DnsResponseTags.GooglePrimary,
            "2s");

        AddPrimaryFallback(
            dns.Rules,
            [RuleSetTags.Cn],
            SingboxTags.LocalDns,
            SingboxTags.LocalTencentDns,
            DnsResponseTags.ChinaPrimary);

        AddPrimaryFallback(
            dns.Rules,
            null,
            SingboxTags.RemoteDns,
            SingboxTags.RemoteGoogleDns,
            DnsResponseTags.GlobalPrimary,
            "2s");
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

    private static void AddPrimaryFallback(
        List<DnsRule> rules,
        List<string>? ruleSet,
        string primaryServer,
        string fallbackServer,
        string responseTag,
        string? primaryTimeout = null)
    {
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            Action = DnsRuleAction.Evaluate,
            Server = primaryServer,
            Tag = responseTag,
            Timeout = primaryTimeout
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = responseTag,
            ResponseRcode = DnsResponseCode.NoError,
            Action = DnsRuleAction.Respond
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = responseTag,
            ResponseRcode = DnsResponseCode.NameError,
            Action = DnsRuleAction.Respond
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            Action = DnsRuleAction.Route,
            Server = fallbackServer
        });
    }
}
