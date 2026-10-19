using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxRouteEmitter
{
    internal static RouteConfig Emit(RoutePlan plan)
    {
        plan.Validate();
        var route = new RouteConfig
        {
            Final = plan.FinalOutbound,
            DefaultHttpClient = HttpClientTags.RuleSetProxy,
            DefaultDomainResolver = ControlPlaneDnsPolicy.CreateResolver(plan.AddressFamily)
        };
        route.RuleSet.AddRange(plan.RuleSets.Select(source => new SingboxRuleSet
        {
            Tag = [.. source.Tags],
            Type = RuleSetType.Remote,
            Format = RuleSetFormat.Binary,
            Url = source.Url,
            UpdateInterval = source.UpdateInterval
        }));
        foreach (RoutePhase phase in plan.Phases)
        {
            DirectForwardingMode? forwarding = phase switch
            {
                RoutePhase.PreSniffDirect => DirectForwardingMode.PreSniff,
                RoutePhase.DomesticUdp443 or RoutePhase.DomesticDirect => DirectForwardingMode.PostUdpSniff,
                _ => null
            };
            foreach (RouteRule rule in CreateRules(phase, plan))
            {
                if (plan.UseDirectForwarding && forwarding is { } mode
                    && rule.Action == RouteRuleAction.Route && rule.Outbound == SingboxTags.DirectOutbound)
                {
                    route.Rules.Add(CreateForwardingRule(rule, mode, useBridge: false));
                    route.Rules.Add(CreateForwardingRule(rule, mode, useBridge: true));
                }
                route.Rules.Add(rule);
            }
        }
        return route;
    }

    private static RouteRule[] CreateRules(RoutePhase phase, RoutePlan plan) => phase switch
    {
        RoutePhase.DnsHijack =>
        [
            new RouteRule
            {
                Type = RouteRuleType.Logical,
                Mode = RouteLogicalMode.And,
                Rules =
                [
                    new RouteRule { Inbound = [SingboxTags.TunInbound] },
                    new RouteRule
                    {
                        Type = RouteRuleType.Logical,
                        Mode = RouteLogicalMode.Or,
                        Rules = [new RouteRule { Protocol = ["dns"] }, new RouteRule { Port = [53] }]
                    }
                ],
                Action = RouteRuleAction.HijackDns
            }
        ],
        RoutePhase.EarlyIpv6Guard => plan.AddressFamily == AddressFamilyPolicy.Ipv4Only ? [CreateIpv6RejectRule()] : [],
        RoutePhase.Tailscale => plan.TailscaleEnabled ?
            [
                new RouteRule
                {
                    Inbound = [SingboxTags.TunInbound],
                    PreferredBy = [SingboxTags.TailscaleEndpoint],
                    Action = RouteRuleAction.Route,
                    Outbound = SingboxTags.TailscaleEndpoint
                }
            ] : [],
        RoutePhase.PreSniffDirect =>
        [
            new RouteRule { IpIsPrivate = true, Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
            new RouteRule { IpCidr = [$"{plan.DirectResolverAddress}/32"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound }
        ],
        RoutePhase.Sniff =>
        [
            new RouteRule
            {
                Type = RouteRuleType.Logical,
                Mode = RouteLogicalMode.And,
                Rules =
                [
                    new RouteRule { Inbound = [SingboxTags.TunInbound], Network = ["tcp"] },
                    new RouteRule { Port = [21, 22, 3306, 5900], Invert = true }
                ],
                Action = RouteRuleAction.Sniff,
                Sniffer = ["http", "tls"],
                Timeout = plan.SniffTimeout
            },
            new RouteRule
            {
                Inbound = [SingboxTags.TunInbound],
                Network = ["udp"],
                Action = RouteRuleAction.Sniff,
                Sniffer = ["quic", "stun"],
                Timeout = plan.SniffTimeout
            }
        ],
        RoutePhase.BlockStunAndAds =>
        [
            new RouteRule
            {
                Inbound = [SingboxTags.TunInbound],
                Protocol = ["stun"],
                Network = ["udp"],
                Action = RouteRuleAction.Reject
            },
            new RouteRule { RuleSet = [RuleSetTags.Ads], Action = RouteRuleAction.Reject }
        ],
        RoutePhase.PriorityUdp443Reject => plan.PriorityServices.Select(service => CreateUdp443RejectRule([.. service.RuleSets])).ToArray(),
        RoutePhase.LateIpv6Guard => plan.AddressFamily == AddressFamilyPolicy.Ipv4Only ? [CreateIpv6RejectRule()] : [],
        RoutePhase.PriorityServices => plan.PriorityServices.Select(CreateServiceRouteRule).ToArray(),
        RoutePhase.DomesticUdp443 =>
        [
            CreateDomesticUdp443DirectRule(RuleSetTags.Cn),
            CreateDomesticUdp443DirectRule(RuleSetTags.CnIp),
            CreateUdp443RejectRule()
        ],
        RoutePhase.OtherServices => plan.OtherServices.Select(CreateServiceRouteRule).ToArray(),
        RoutePhase.DomesticDirect =>
        [
            new RouteRule { RuleSet = [RuleSetTags.Cn], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
            new RouteRule { RuleSet = [RuleSetTags.CnIp], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound }
        ],
        RoutePhase.FinalOutbound => [],
        _ => throw new InvalidOperationException("Unknown route phase.")
    };

    private enum DirectForwardingMode
    {
        PreSniff,
        PostUdpSniff
    }

    private static RouteRule CreateForwardingRule(
        RouteRule directRule,
        DirectForwardingMode forwardingMode,
        bool useBridge) => directRule with
        {
            Inbound = [SingboxTags.TunInbound],
            Network = forwardingMode == DirectForwardingMode.PostUdpSniff ? ["udp"] : directRule.Network,
            PreferredBy = useBridge ? [SingboxTags.BridgeOutbound] : null,
            Action = useBridge ? RouteRuleAction.Route : RouteRuleAction.Bypass,
            Outbound = useBridge ? SingboxTags.BridgeOutbound : null
        };

    private static RouteRule CreateIpv6RejectRule() =>
        new()
        {
            IpVersion = 6,
            Action = RouteRuleAction.Reject
        };

    private static RouteRule CreateDomesticUdp443DirectRule(string ruleSet) =>
        new()
        {
            Inbound = [SingboxTags.TunInbound],
            Port = [443],
            Network = ["udp"],
            RuleSet = [ruleSet],
            Action = RouteRuleAction.Route,
            Outbound = SingboxTags.DirectOutbound
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

    private static RouteRule CreateServiceRouteRule(ServiceDefinition service) =>
        new()
        {
            RuleSet = [.. service.RuleSets],
            Action = RouteRuleAction.Route,
            Outbound = service.Name
        };
}
