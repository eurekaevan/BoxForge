using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed class RouteProfileBuilder(
    IOptions<TailscaleOptions> tailscaleOptions)
{
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public RouteConfig Build(TargetPlatform platform) => Build(platform, AddressFamilyPolicies.For(platform));

    public RouteConfig Build(TargetPlatform platform, AddressFamilyPolicy addressFamily)
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
            DefaultHttpClient = HttpClientTags.RuleSetProxy,
            DefaultDomainResolver = ControlPlaneDnsPolicy.CreateResolver(addressFamily)
        };

        route.RuleSet.AddRange([
            CreateDustinWinRuleSetGroup(
                [
                    RuleSetTags.Ads,
                    RuleSetTags.Ai,
                    RuleSetTags.Spotify,
                    RuleSetTags.Games,
                    RuleSetTags.Cn,
                    RuleSetTags.CnIp
                ]),
            CreateMetaCubeXRuleSet(RuleSetTags.Google, "google"),
            CreateMetaCubeXRuleSet(RuleSetTags.Microsoft, "microsoft")
        ]);

        var rules = new List<RouteRule>
        {
            new() {
                Type = RouteRuleType.Logical,
                Mode = RouteLogicalMode.And,
                Rules =
                [
                    new RouteRule { Inbound = [SingboxTags.TunInbound] },
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
        if (addressFamily == AddressFamilyPolicy.Ipv4Only)
        {
            rules.Add(CreateIpv6RejectRule());
        }

        if (tailscale.IsEnabled(platform))
        {
            // 必须位于私网直连规则之前，才能覆盖 tailnet 通告的私有子网路由。
            rules.Add(new RouteRule
            {
                Inbound = [SingboxTags.TunInbound],
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
            new()
            {
                Type = RouteRuleType.Logical,
                Mode = RouteLogicalMode.And,
                Rules =
                [
                    new RouteRule { Inbound = [SingboxTags.TunInbound], Network = ["tcp"] },
                    // Skip common server-first services without bypassing later routing policy.
                    new RouteRule { Port = [21, 22, 3306, 5900], Invert = true }
                ],
                Action = RouteRuleAction.Sniff,
                Sniffer = ["http", "tls"],
                Timeout = RouteTuningPolicy.SniffTimeout
            },
            new()
            {
                Inbound = [SingboxTags.TunInbound],
                Network = ["udp"],
                Action = RouteRuleAction.Sniff,
                Sniffer = ["quic", "stun"],
                Timeout = RouteTuningPolicy.SniffTimeout
            },
            new()
            {
                Inbound = [SingboxTags.TunInbound],
                Protocol = ["stun"],
                Network = ["udp"],
                Action = RouteRuleAction.Reject
            },
            new()
            {
                RuleSet = [RuleSetTags.Ads],
                Action = RouteRuleAction.Reject
            }
        ]);

        var prioritizedServices = ProfileDefinitions.Services.Where(
            service => service.PrecedesDomesticRoutes
                && service.RuleSets.Length > 0).ToList();
        foreach (var service in prioritizedServices)
        {
            rules.Add(CreateUdp443RejectRule([.. service.RuleSets]));
        }

        // Preserve the late IPv6 guard before service routing as defense in depth.
        if (addressFamily == AddressFamilyPolicy.Ipv4Only)
        {
            rules.Add(CreateIpv6RejectRule());
        }

        foreach (var service in prioritizedServices)
        {
            rules.Add(CreateServiceRouteRule(service));
        }

        rules.AddRange([
            MarkDirectForwarding(
                CreateDomesticUdp443DirectRule([RuleSetTags.Cn], SingboxTags.DirectOutbound),
                DirectForwardingMode.PostUdpSniff),
            MarkDirectForwarding(
                CreateDomesticUdp443DirectRule([RuleSetTags.CnIp], SingboxTags.DirectOutbound),
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
                new RouteRule { RuleSet = [RuleSetTags.Cn], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                DirectForwardingMode.PostUdpSniff),
            MarkDirectForwarding(
                new RouteRule { RuleSet = [RuleSetTags.CnIp], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                DirectForwardingMode.PostUdpSniff)
        ]);

        route.Rules.AddRange(AddDirectForwardingLayers(
            rules,
            PlatformProfile.For(platform).UseDirectForwarding,
            directForwardingModes));
        return route;
    }

    private static IEnumerable<RouteRule> AddDirectForwardingLayers(
        IEnumerable<RouteRule> rules,
        bool useDirectForwarding,
        IReadOnlyDictionary<RouteRule, DirectForwardingMode> forwardingModes)
    {
        foreach (RouteRule rule in rules)
        {
            if (useDirectForwarding
                && forwardingModes.TryGetValue(
                    rule,
                    out DirectForwardingMode forwardingMode))
            {
                EnsureDirectForwardingRule(rule);
                yield return CreateBypassRule(rule, forwardingMode);
                yield return CreateBridgeRouteRule(rule, forwardingMode);
            }

            yield return rule;
        }
    }

    private static void EnsureDirectForwardingRule(RouteRule rule)
    {
        if (rule.Type == RouteRuleType.Logical
            || rule.Action != RouteRuleAction.Route
            || rule.Outbound != SingboxTags.DirectOutbound)
        {
            throw new InvalidOperationException(
                "Only non-logical DIRECT route rules can opt in to direct forwarding.");
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

    private static RouteRule CreateIpv6RejectRule() =>
        new()
        {
            IpVersion = 6,
            Action = RouteRuleAction.Reject
        };

    private static RouteRule CreateDomesticUdp443DirectRule(
        List<string> ruleSets,
        string directOutbound) =>
        new()
        {
            Inbound = [SingboxTags.TunInbound],
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
            Inbound = [SingboxTags.TunInbound],
            Port = [443],
            Network = ["udp"],
            RuleSet = ruleSets,
            Action = RouteRuleAction.Reject,
            NoDrop = true
        };

    private enum DirectForwardingMode
    {
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

    private static SingboxRuleSet CreateDustinWinRuleSetGroup(
        List<string> tags) => CreateRemoteBinaryRuleSet(
            tags,
            "https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs");

    private static SingboxRuleSet CreateMetaCubeXRuleSet(
        string tag,
        string fileName) => CreateRemoteBinaryRuleSet(
            [tag],
            $"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/sing/geo/geosite/{fileName}.srs");

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
