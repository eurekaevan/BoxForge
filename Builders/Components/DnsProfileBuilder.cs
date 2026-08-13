using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public class DnsProfileBuilder(
    IOptions<SingboxOptions> singboxOptions,
    IOptions<TailscaleOptions> tailscaleOptions)
{
    private readonly SingboxOptions singbox = singboxOptions.Value;
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public DnsConfig Build(NodeCatalog nodes)
    {
        var dns = new DnsConfig();

        dns.Servers.AddRange([
            new LocalDnsServer { Tag = SingboxTags.BootstrapDns },
            CreateHttpsServer(SingboxTags.NodeResolverDns, "223.5.5.5", "dns.alidns.com"),
            CreateHttpsServer(SingboxTags.LocalTencentDns, "119.29.29.29", "doh.pub"),
            CreateHttpsServer(SingboxTags.LocalDns, "223.5.5.5", "dns.alidns.com"),
            CreateHttpsServer(
                SingboxTags.RemoteGoogleDns,
                "8.8.8.8",
                "dns.google",
                singbox.MainProxyGroup),
            CreateHttpsServer(
                SingboxTags.RemoteDns,
                "1.1.1.1",
                "cloudflare-dns.com",
                singbox.MainProxyGroup)
        ]);

        if (tailscale.Enabled)
        {
            dns.Servers.Add(new TailscaleDnsServer
            {
                Tag = tailscale.DnsTag,
                EndpointTag = tailscale.Tag,
                AcceptDefaultResolversValue = false
            });

            // sing-box 1.14 直接根据 Tailscale 的 MagicDNS 域名与分流后缀匹配。
            dns.Rules.Add(new DnsRule
            {
                PreferredBy = [tailscale.DnsTag],
                Action = DnsRuleAction.Route,
                Server = tailscale.DnsTag,
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
            RuleSet = [SingboxOptions.AdGuardDnsRuleSetTag],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.NameError
        });

        AddRace(
            dns.Rules,
            ["geosite-cn", "geosite-category-pt"],
            SingboxTags.LocalTencentDns,
            SingboxTags.LocalDns,
            "cn");

        // 国内域名先由本地 DNS 返回 A/AAAA；其余 AAAA 仍返回空结果，
        // 防止非国内公网 IPv6 绕过后续的 IPv6 拒绝策略。
        dns.Rules.Add(new DnsRule
        {
            QueryType = ["AAAA"],
            Action = DnsRuleAction.Predefined,
            Rcode = DnsResponseCode.NoError
        });

        AddRace(
            dns.Rules,
            null,
            SingboxTags.RemoteGoogleDns,
            SingboxTags.RemoteDns,
            "global");
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

    private static void AddRace(
        List<DnsRule> rules,
        List<string>? ruleSet,
        string firstServer,
        string secondServer,
        string responseTagPrefix)
    {
        string firstResponseTag = $"{responseTagPrefix}-first";
        string secondResponseTag = $"{responseTagPrefix}-second";

        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            Action = DnsRuleAction.Evaluate,
            Server = firstServer,
            Tag = firstResponseTag
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = firstResponseTag,
            IpAcceptAny = true,
            Action = DnsRuleAction.Respond,
            Race = true
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            Action = DnsRuleAction.Evaluate,
            Server = secondServer,
            Tag = secondResponseTag,
            Speculative = true
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = secondResponseTag,
            IpAcceptAny = true,
            Action = DnsRuleAction.Respond,
            Race = true
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = firstResponseTag,
            ResponseRcode = DnsResponseCode.NameError,
            Action = DnsRuleAction.Respond
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = secondResponseTag,
            ResponseRcode = DnsResponseCode.NameError,
            Action = DnsRuleAction.Respond
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = secondResponseTag,
            Action = DnsRuleAction.Respond
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            Action = DnsRuleAction.Route,
            Server = secondServer
        });
    }
}
