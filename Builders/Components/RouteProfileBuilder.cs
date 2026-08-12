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
                Type = RouteRuleType.Logical,
                Mode = RouteLogicalMode.And,
                Rules =
                [
                    new RouteRule { Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound] },
                    new RouteRule
                    {
                        Type = RouteRuleType.Logical,
                        Mode = RouteLogicalMode.Or,
                        Rules = [ new RouteRule { Protocol = ["dns"] }, new RouteRule { Port = [53] } ]
                    }
                ],
                Action = RouteRuleAction.HijackDns
            }
        };

        if (tailscale.Enabled)
        {
            // 必须位于私网直连规则之前，才能覆盖 tailnet 通告的私有子网路由。
            rules.Add(new RouteRule
            {
                Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound],
                PreferredBy = [tailscale.Tag],
                Action = RouteRuleAction.Route,
                Outbound = tailscale.Tag
            });
        }

        rules.AddRange([
            new RouteRule { IpIsPrivate = true, Action = RouteRuleAction.Route, Outbound = singbox.Direct },
            new() { IpCidr = ["::/0"], Action = RouteRuleAction.Reject },
            new() { IpCidr = ["223.5.5.5/32"], Action = RouteRuleAction.Route, Outbound = singbox.Direct },
            new() { Port = [3478, 3479, 19302, 19303], Network = ["udp"], Action = RouteRuleAction.Reject },
            CreateSniffRule("tcp", ["http", "tls"]),
            CreateSniffRule("udp", ["quic"]),
            new() { RuleSet = ["geosite-category-ads-all"], Action = RouteRuleAction.Reject }
        ]);

        foreach (var service in ProfileDefinitions.Services)
        {
            if (service.RuleSets.Length > 0)
            {
                rules.Add(new RouteRule
                {
                    RuleSet = [.. service.RuleSets],
                    Action = RouteRuleAction.Route,
                    Outbound = service.Name
                });
            }
        }

        rules.AddRange([
            CreateDomesticUdp443RejectRule(["geosite-cn", "geosite-category-pt"]),
            new RouteRule { RuleSet = ["geosite-cn", "geosite-category-pt"], Action = RouteRuleAction.Route, Outbound = singbox.Direct },
            new RouteRule { Inbound = [SingboxTags.MixedInbound], Action = RouteRuleAction.Resolve },
            CreateDomesticUdp443RejectRule(["geoip-cn"]),
            new RouteRule { RuleSet = ["geoip-cn"], Action = RouteRuleAction.Route, Outbound = singbox.Direct }
        ]);

        route.Rules.AddRange(rules);
        return route;
    }

    private static RouteRule CreateSniffRule(string network, List<string> sniffers) =>
        new()
        {
            Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound],
            Network = [network],
            Action = RouteRuleAction.Sniff,
            Sniffer = sniffers,
            Timeout = "300ms"
        };

    private static RouteRule CreateDomesticUdp443RejectRule(List<string> ruleSets) =>
        new()
        {
            Type = RouteRuleType.Logical,
            Mode = RouteLogicalMode.And,
            Rules =
            [
                new RouteRule { Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound] },
                new RouteRule { Port = [443], Network = ["udp"] },
                new RouteRule { RuleSet = ruleSets }
            ],
            Action = RouteRuleAction.Reject
        };

    private static SingboxRuleSet CreateRemoteRuleSet(
        string tag,
        string repoType,
        string fileName) => new()
        {
            Tag = tag,
            Type = RuleSetType.Remote,
            Format = RuleSetFormat.Binary,
            Url = $"https://fastly.jsdelivr.net/gh/SagerNet/sing-{repoType}@rule-set/{fileName}.srs",
            UpdateInterval = "1d"
        };
}
