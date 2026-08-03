using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public class RouteProfileBuilder(
    IOptions<SingboxOptions> singboxOptions,
    IOptions<TailscaleOptions> tailscaleOptions)
{
    private readonly SingboxOptions singbox = singboxOptions.Value;
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public RouteConfig Build()
    {
        var route = new RouteConfig
        {
            Final = singbox.MainProxyGroup,
            DefaultHttpClient = SingboxOptions.RuleSetHttpClientTag
        };

        route.RuleSet.AddRange([
            CreateRemoteRuleSet("geosite-category-ads-all", "geosite", "geosite-category-ads-all"),
            CreateRemoteRuleSet("geosite-category-pt", "geosite", "geosite-category-pt"),
            CreateRemoteRuleSet("geosite-cn", "geosite", "geosite-cn"),
            CreateRemoteRuleSet("geoip-cn", "geoip", "geoip-cn"),
            CreateRemoteRuleSet("geosite-spotify", "geosite", "geosite-spotify"),
            CreateRemoteRuleSet("geosite-steam", "geosite", "geosite-steam"),
            CreateRemoteRuleSet("geosite-category-ai-!cn", "geosite", "geosite-category-ai-!cn"),
            CreateRemoteRuleSet("geosite-microsoft", "geosite", "geosite-microsoft"),
        ]);

        var rules = new List<RouteRule>
        {
            new() {
                Type = "logical",
                Mode = "and",
                Rules =
                [
                    new RouteRule { Inbound = ["tun-in", "mixed-in"] },
                    new RouteRule
                    {
                        Type = "logical",
                        Mode = "or",
                        Rules = [ new RouteRule { Protocol = ["dns"] }, new RouteRule { Port = [53] } ]
                    }
                ],
                Action = "hijack-dns"
            }
        };

        if (tailscale.Enabled)
        {
            // 必须位于私网直连规则之前，才能覆盖 tailnet 通告的私有子网路由。
            rules.Add(new RouteRule
            {
                Inbound = ["tun-in", "mixed-in"],
                PreferredBy = [tailscale.Tag],
                Action = "route",
                Outbound = tailscale.Tag
            });
        }

        rules.AddRange([
            new RouteRule { IpIsPrivate = true, Action = "route", Outbound = singbox.Direct },
            new() { IpCidr = ["::/0"], Action = "reject" },
            new() { IpCidr = ["223.5.5.5/32"], Action = "route", Outbound = singbox.Direct },
            new() { Port = [3478, 3479, 19302, 19303], Network = ["udp"], Action = "reject" },
            new() { Inbound = ["tun-in", "mixed-in"], Port = [443], Network = ["udp"], Action = "reject" },
            new() { Inbound = ["tun-in", "mixed-in"], Action = "sniff", Timeout = "300ms" },
            new() { Protocol = ["ssh"], Action = "route", Outbound = singbox.Direct },
            new() { RuleSet = ["geosite-category-ads-all"], Action = "reject" }
        ]);

        foreach (var service in ProfileDefinitions.Services)
        {
            if (service.RuleSets.Count > 0)
            {
                rules.Add(new RouteRule
                {
                    RuleSet = service.RuleSets,
                    Action = "route",
                    Outbound = service.Name
                });
            }
        }

        rules.AddRange([
            new RouteRule { RuleSet = ["geosite-cn", "geosite-category-pt"], Action = "route", Outbound = singbox.Direct },
            new RouteRule { Inbound = ["mixed-in"], Action = "resolve" },
            new RouteRule { RuleSet = ["geoip-cn"], Action = "route", Outbound = singbox.Direct }
        ]);

        route.Rules.AddRange(rules);
        return route;
    }

    private SingboxRuleSet CreateRemoteRuleSet(string tag, string repoType, string fileName) => new()
    {
        Tag = tag,
        Type = "remote",
        Format = "binary",
        Url = $"https://fastly.jsdelivr.net/gh/SagerNet/sing-{repoType}@rule-set/{fileName}.srs",
        UpdateInterval = "1d"
    };
}
