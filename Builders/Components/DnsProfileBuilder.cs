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
            new HttpsDnsServer { Tag = "node-resolver", ServerAddress = "223.5.5.5" },
            new HttpsDnsServer { Tag = "remote", ServerAddress = "1.1.1.1", DetourTag = singbox.MainProxyGroup },
            new HttpsDnsServer { Tag = "local", ServerAddress = "223.5.5.5" }
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
                Server = tailscale.DnsTag
            });
        }

        dns.Rules.Add(new DnsRule { QueryType = ["AAAA"], Action = "predefined", Rcode = "NOERROR" });
        dns.Rules.Add(new DnsRule { RuleSet = ["geosite-category-ads-all"], Action = "predefined", Rcode = "NOERROR" });

        if (nodes.ServerDomains.Count > 0)
        {
            dns.Rules.Add(new DnsRule { Domain = [.. nodes.ServerDomains], Action = "route", Server = "node-resolver" });
        }

        dns.Rules.Add(new DnsRule { RuleSet = ["geosite-cn", "geosite-category-pt"], Action = "route", Server = "local" });
        return dns;
    }
}
