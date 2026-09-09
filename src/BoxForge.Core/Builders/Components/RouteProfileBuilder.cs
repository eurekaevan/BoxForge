using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed class RouteProfileBuilder(
    IOptions<TailscaleOptions> tailscaleOptions)
{
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public RouteConfig Build(TargetPlatform platform)
    {
        var route = new RouteConfig
        {
            Final = SingboxTags.MainProxyGroup,
            DefaultHttpClient = HttpClientTags.RuleSetDirect
        };

        route.RuleSet.AddRange([
            CreateRemoteRuleSetGroup(
                [
                    AdBlockingRuleSets.SagerAdsTag,
                    "geosite-category-pt",
                    "geosite-google",
                    "geosite-cn",
                    "geosite-spotify",
                    "geosite-steam",
                    "geosite-category-ai-!cn",
                    "geosite-microsoft"
                ],
                "geosite"),
            CreateRemoteRuleSet("geoip-cn", "geoip", "geoip-cn"),
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

        if (tailscale.IsEnabled(platform))
        {
            // 必须位于私网直连规则之前，才能覆盖 tailnet 通告的私有子网路由。
            rules.Add(new RouteRule
            {
                Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound],
                PreferredBy = [SingboxTags.TailscaleEndpoint],
                Action = RouteRuleAction.Route,
                Outbound = SingboxTags.TailscaleEndpoint
            });
        }

        rules.AddRange([
            new RouteRule { IpIsPrivate = true, Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
            new() { IpCidr = ["223.5.5.5/32"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
            CreateSniffRule("tcp", ["http", "tls"]),
            CreateSniffRule("udp", ["quic", "stun"]),
            new()
            {
                Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound],
                Protocol = ["stun"],
                Network = ["udp"],
                Action = RouteRuleAction.Reject
            },
            new()
            {
                RuleSet = [AdBlockingRuleSets.SagerAdsTag],
                Action = RouteRuleAction.Reject
            }
        ]);

        List<string> proxyServiceRuleSets =
        [
            .. ProfileDefinitions.Services
                .SelectMany(service => service.RuleSets)
                .Distinct(StringComparer.Ordinal)
        ];
        rules.Add(CreateMixedResolveRule(
            proxyServiceRuleSets,
            DnsStrategy.Ipv4Only));
        rules.Add(CreateMixedResolveRule(
            ["geosite-cn", "geosite-category-pt"],
            DnsStrategy.PreferIpv4));

        var prioritizedServices = ProfileDefinitions.Services.Where(
            service => service.PrecedesDomesticRoutes
                && service.RuleSets.Length > 0).ToList();
        foreach (var service in prioritizedServices)
        {
            rules.Add(CreateUdp443RejectRule([.. service.RuleSets]));
        }

        rules.AddRange([
            CreateDomesticIpv6DirectRule(SingboxTags.DirectOutbound),
            new() { IpCidr = ["::/0"], Action = RouteRuleAction.Reject }
        ]);

        foreach (var service in prioritizedServices)
        {
            rules.Add(CreateServiceRouteRule(service));
        }

        rules.AddRange([
            CreateDomesticUdp443DirectRule(["geosite-cn", "geosite-category-pt"], SingboxTags.DirectOutbound),
            new RouteRule
            {
                Inbound = [SingboxTags.MixedInbound],
                Port = [443],
                Network = ["udp"],
                Action = RouteRuleAction.Resolve,
                Strategy = DnsStrategy.Ipv4Only
            },
            CreateDomesticUdp443DirectRule(["geoip-cn"], SingboxTags.DirectOutbound),
            CreateUdp443RejectRule()
        ]);

        foreach (var service in ProfileDefinitions.Services.Where(
            service => !service.PrecedesDomesticRoutes
                && service.RuleSets.Length > 0))
        {
            rules.Add(CreateServiceRouteRule(service));
        }

        rules.AddRange([
            new RouteRule { RuleSet = ["geosite-cn", "geosite-category-pt"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
            new RouteRule
            {
                Inbound = [SingboxTags.MixedInbound],
                Action = RouteRuleAction.Resolve,
                Strategy = DnsStrategy.Ipv4Only
            },
            new RouteRule
            {
                Inbound = [SingboxTags.MixedInbound],
                IpIsPrivate = true,
                Action = RouteRuleAction.Route,
                Outbound = SingboxTags.DirectOutbound
            },
            new RouteRule { RuleSet = ["geoip-cn"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound }
        ]);

        route.Rules.AddRange(AddDirectForwardingLayers(rules, platform));
        return route;
    }

    private static IEnumerable<RouteRule> AddDirectForwardingLayers(
        IEnumerable<RouteRule> rules,
        TargetPlatform platform)
    {
        bool beforeSniff = true;
        foreach (RouteRule rule in rules)
        {
            if (rule.Action == RouteRuleAction.Sniff)
            {
                beforeSniff = false;
            }

            bool supportsL3Direct = beforeSniff
                && platform != TargetPlatform.Android
                && rule.Type == null
                && rule.Action == RouteRuleAction.Route
                && rule.Outbound == SingboxTags.DirectOutbound
                && (rule.Inbound == null
                    || rule.Inbound.Contains(SingboxTags.TunInbound));
            if (supportsL3Direct)
            {
                if (platform == TargetPlatform.Linux)
                {
                    yield return CreateBypassRule(rule);
                }

                yield return CreateBridgeRouteRule(rule);
            }

            yield return rule;
        }
    }

    private static RouteRule CreateBridgeRouteRule(RouteRule directRule)
        => directRule with
        {
            Inbound = [SingboxTags.TunInbound],
            PreferredBy = [SingboxTags.BridgeOutbound],
            Outbound = SingboxTags.BridgeOutbound
        };

    private static RouteRule CreateBypassRule(RouteRule directRule)
        => directRule with
        {
            Inbound = [SingboxTags.TunInbound],
            Action = RouteRuleAction.Bypass,
            Outbound = null
        };

    private static RouteRule CreateSniffRule(string network, List<string> sniffers) =>
        new()
        {
            Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound],
            Network = [network],
            Action = RouteRuleAction.Sniff,
            Sniffer = sniffers,
            Timeout = "300ms"
        };

    private static RouteRule CreateDomesticIpv6DirectRule(string directOutbound) =>
        new()
        {
            Type = RouteRuleType.Logical,
            Mode = RouteLogicalMode.And,
            Rules =
            [
                new RouteRule { IpCidr = ["::/0"] },
                new RouteRule
                {
                    RuleSet = ["geoip-cn"]
                }
            ],
            Action = RouteRuleAction.Route,
            Outbound = directOutbound
        };

    private static RouteRule CreateMixedResolveRule(
        List<string> ruleSets,
        DnsStrategy strategy) =>
        new()
        {
            Inbound = [SingboxTags.MixedInbound],
            RuleSet = ruleSets,
            Action = RouteRuleAction.Resolve,
            Strategy = strategy
        };

    private static RouteRule CreateDomesticUdp443DirectRule(
        List<string> ruleSets,
        string directOutbound) =>
        new()
        {
            Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound],
            Port = [443],
            Network = ["udp"],
            RuleSet = ruleSets,
            Action = RouteRuleAction.Route,
            Outbound = directOutbound
        };

    private static RouteRule CreateUdp443RejectRule(
        List<string>? ruleSets = null) =>
        new()
        {
            Inbound = [SingboxTags.TunInbound, SingboxTags.MixedInbound],
            Port = [443],
            Network = ["udp"],
            RuleSet = ruleSets,
            Action = RouteRuleAction.Reject
        };

    private static RouteRule CreateServiceRouteRule(ServiceDefinition service) =>
        new()
        {
            RuleSet = [.. service.RuleSets],
            Action = RouteRuleAction.Route,
            Outbound = service.Name
        };

    private static SingboxRuleSet CreateRemoteRuleSet(
        string tag,
        string repoType,
        string fileName) => CreateRemoteBinaryRuleSet(
            [tag],
            $"https://fastly.jsdelivr.net/gh/SagerNet/sing-{repoType}@rule-set/{fileName}.srs");

    private static SingboxRuleSet CreateRemoteRuleSetGroup(
        List<string> tags,
        string repoType) => CreateRemoteBinaryRuleSet(
            tags,
            $"https://fastly.jsdelivr.net/gh/SagerNet/sing-{repoType}@rule-set/{{tag}}.srs");

    private static SingboxRuleSet CreateRemoteBinaryRuleSet(
        List<string> tags,
        string url) => new()
        {
            Tag = tags,
            Type = RuleSetType.Remote,
            Format = RuleSetFormat.Binary,
            Url = url,
            UpdateInterval = "1d"
        };
}
