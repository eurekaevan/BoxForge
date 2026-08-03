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
            new LocalDnsServer { Tag = "bootstrap" },
            CreateHttpsServer("node-resolver", "223.5.5.5", "dns.alidns.com"),
            CreateHttpsServer("local-tencent", "119.29.29.29", "doh.pub"),
            CreateHttpsServer("local", "223.5.5.5", "dns.alidns.com"),
            CreateHttpsServer(
                "remote-google",
                "8.8.8.8",
                "dns.google",
                singbox.MainProxyGroup),
            CreateHttpsServer(
                "remote",
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
                Action = "route",
                Server = tailscale.DnsTag,
                DisableOptimisticCache = true
            });
        }

        dns.Rules.Add(new DnsRule { QueryType = ["AAAA"], Action = "predefined", Rcode = "NOERROR" });
        dns.Rules.Add(new DnsRule { RuleSet = ["geosite-category-ads-all"], Action = "predefined", Rcode = "NOERROR" });

        if (nodes.ServerDomains.Count > 0)
        {
            dns.Rules.Add(new DnsRule
            {
                Domain = [.. nodes.ServerDomains],
                Action = "route",
                Server = "node-resolver",
                DisableOptimisticCache = true
            });
        }

        AddRace(
            dns.Rules,
            ["geosite-cn", "geosite-category-pt"],
            "local-tencent",
            "local",
            "cn");

        AddRace(
            dns.Rules,
            null,
            "remote-google",
            "remote",
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
            Action = "evaluate",
            Server = firstServer,
            Tag = firstResponseTag
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = firstResponseTag,
            IpAcceptAny = true,
            Action = "respond",
            Race = true
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            Action = "evaluate",
            Server = secondServer,
            Tag = secondResponseTag,
            Speculative = true
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = secondResponseTag,
            IpAcceptAny = true,
            Action = "respond",
            Race = true
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = firstResponseTag,
            ResponseRcode = "NXDOMAIN",
            Action = "respond"
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = secondResponseTag,
            ResponseRcode = "NXDOMAIN",
            Action = "respond"
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            MatchResponse = secondResponseTag,
            Action = "respond"
        });
        rules.Add(new DnsRule
        {
            RuleSet = ruleSet,
            Action = "route",
            Server = secondServer
        });
    }
}
