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
        var directForwardingModes = new Dictionary<RouteRule, DirectForwardingMode>(
            ReferenceEqualityComparer.Instance);
        RouteRule MarkDirectForwarding(
            RouteRule rule,
            DirectForwardingMode mode)
        {
            directForwardingModes.Add(rule, mode);
            return rule;
        }

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
            MarkDirectForwarding(
                new RouteRule { IpIsPrivate = true, Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                DirectForwardingMode.PreSniff),
            MarkDirectForwarding(
                new RouteRule { IpCidr = ["223.5.5.5/32"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                DirectForwardingMode.PreSniff),
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
            MarkDirectForwarding(
                CreateDomesticIpv6DirectRule(SingboxTags.DirectOutbound),
                DirectForwardingMode.PostUdpSniff),
            new() { IpVersion = 6, Action = RouteRuleAction.Reject }
        ]);

        foreach (var service in prioritizedServices)
        {
            rules.Add(CreateServiceRouteRule(service));
        }

        rules.AddRange([
            MarkDirectForwarding(
                CreateDomesticUdp443DirectRule(["geosite-cn", "geosite-category-pt"], SingboxTags.DirectOutbound),
                DirectForwardingMode.PostUdpSniff),
            new RouteRule
            {
                Inbound = [SingboxTags.MixedInbound],
                Port = [443],
                Network = ["udp"],
                Action = RouteRuleAction.Resolve,
                Strategy = DnsStrategy.Ipv4Only
            },
            MarkDirectForwarding(
                CreateDomesticUdp443DirectRule(["geoip-cn"], SingboxTags.DirectOutbound),
                DirectForwardingMode.PostUdpSniff),
            CreateUdp443RejectRule()
        ]);

        foreach (var service in ProfileDefinitions.Services.Where(
            service => !service.PrecedesDomesticRoutes
                && service.RuleSets.Length > 0))
        {
            rules.Add(CreateServiceRouteRule(service));
        }

        rules.AddRange([
            MarkDirectForwarding(
                new RouteRule { RuleSet = ["geosite-cn", "geosite-category-pt"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                DirectForwardingMode.PostUdpSniff),
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
            MarkDirectForwarding(
                new RouteRule { RuleSet = ["geoip-cn"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                DirectForwardingMode.PostUdpSniff)
        ]);

        route.Rules.AddRange(AddDirectForwardingLayers(
            rules,
            platform,
            directForwardingModes));
        return route;
    }

    private static IEnumerable<RouteRule> AddDirectForwardingLayers(
        IEnumerable<RouteRule> rules,
        TargetPlatform platform,
        IReadOnlyDictionary<RouteRule, DirectForwardingMode> forwardingModes)
    {
        foreach (RouteRule rule in rules)
        {
            if (platform != TargetPlatform.Android
                && forwardingModes.TryGetValue(
                    rule,
                    out DirectForwardingMode forwardingMode)
                && forwardingMode != DirectForwardingMode.None)
            {
                EnsureDirectForwardingRule(rule);
                if (platform == TargetPlatform.Linux)
                {
                    yield return CreateBypassRule(rule, forwardingMode);
                }

                yield return CreateBridgeRouteRule(rule, forwardingMode);
            }

            yield return rule;
        }
    }

    private static void EnsureDirectForwardingRule(RouteRule rule)
    {
        if (rule.Action != RouteRuleAction.Route
            || rule.Outbound != SingboxTags.DirectOutbound)
        {
            throw new InvalidOperationException(
                "Only DIRECT route rules can opt in to direct forwarding.");
        }
    }

    private static RouteRule CreateBridgeRouteRule(
        RouteRule directRule,
        DirectForwardingMode forwardingMode) => CreateForwardingRule(
            directRule,
            forwardingMode,
            RouteRuleAction.Route,
            SingboxTags.BridgeOutbound,
            useBridgeGate: true);

    private static RouteRule CreateBypassRule(
        RouteRule directRule,
        DirectForwardingMode forwardingMode) => CreateForwardingRule(
            directRule,
            forwardingMode,
            RouteRuleAction.Bypass,
            outbound: null,
            useBridgeGate: false);

    private static RouteRule CreateForwardingRule(
        RouteRule directRule,
        DirectForwardingMode forwardingMode,
        RouteRuleAction action,
        string? outbound,
        bool useBridgeGate)
    {
        List<string>? network = forwardingMode == DirectForwardingMode.PostUdpSniff
            ? ["udp"]
            : directRule.Network;
        if (directRule.Type != RouteRuleType.Logical)
        {
            return directRule with
            {
                Inbound = [SingboxTags.TunInbound],
                Network = network,
                PreferredBy = useBridgeGate
                    ? [SingboxTags.BridgeOutbound]
                    : null,
                Action = action,
                Outbound = outbound
            };
        }

        if (directRule.Mode != RouteLogicalMode.And || directRule.Rules == null)
        {
            throw new InvalidOperationException(
                "Direct forwarding only supports logical AND rules.");
        }

        var matchers = new List<RouteRule>(directRule.Rules)
        {
            new() { Inbound = [SingboxTags.TunInbound] }
        };
        if (network != null)
        {
            matchers.Add(new RouteRule { Network = network });
        }
        if (useBridgeGate)
        {
            matchers.Add(new RouteRule
            {
                PreferredBy = [SingboxTags.BridgeOutbound]
            });
        }

        return directRule with
        {
            Rules = matchers,
            Action = action,
            Outbound = outbound
        };
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

    private static RouteRule CreateDomesticIpv6DirectRule(string directOutbound) =>
        new()
        {
            Type = RouteRuleType.Logical,
            Mode = RouteLogicalMode.And,
            Rules =
            [
                new RouteRule { IpVersion = 6 },
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
            Action = RouteRuleAction.Reject,
            NoDrop = true
        };

    private enum DirectForwardingMode
    {
        None,
        PreSniff,
        PostUdpSniff
    }

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
