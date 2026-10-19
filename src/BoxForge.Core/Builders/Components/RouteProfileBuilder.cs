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
        var route = new RouteConfig
        {
            Final = SingboxTags.MainProxyGroup,
            DefaultHttpClient = HttpClientTags.RuleSetProxy,
            DefaultDomainResolver = ControlPlaneDnsPolicy.CreateResolver(addressFamily)
        };

        route.RuleSet.AddRange([
            CreateRemoteBinaryRuleSet(
                [
                    RuleSetTags.Ai,
                    RuleSetTags.Spotify,
                    RuleSetTags.Games,
                    RuleSetTags.Cn,
                    RuleSetTags.CnIp
                ],
                "https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/{tag}.srs"),
            CreateMetaCubeXRuleSet(RuleSetTags.Ads, "category-ads-and-telemetry"),
            CreateMetaCubeXRuleSet(RuleSetTags.Google, "google"),
            CreateMetaCubeXRuleSet(RuleSetTags.Microsoft, "microsoft")
        ]);

        IReadOnlyList<RuleStage> stages = CreateRulePlan(addressFamily, tailscale.IsEnabled(platform));
        ValidateRulePlan(stages);
        route.Rules.AddRange(ExpandDirectForwarding(stages, PlatformProfile.For(platform).UseDirectForwarding));
        return route;
    }

    internal static IReadOnlyList<RuleStage> CreateRulePlan(
        AddressFamilyPolicy addressFamily,
        bool tailscaleEnabled)
    {
        var prioritizedServices = ProfileDefinitions.Services
            .Where(service => service.PrecedesDomesticRoutes && service.RuleSets.Length > 0).ToArray();
        var otherServices = ProfileDefinitions.Services
            .Where(service => !service.PrecedesDomesticRoutes && service.RuleSets.Length > 0).ToArray();
        return
        [
            new(RoutePhase.DnsHijack, null,
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
            ]),
            new(RoutePhase.EarlyIpv6Guard, RoutePhase.DnsHijack,
                addressFamily == AddressFamilyPolicy.Ipv4Only ? [CreateIpv6RejectRule()] : []),
            new(RoutePhase.Tailscale, RoutePhase.EarlyIpv6Guard,
                tailscaleEnabled ?
                [
                    new RouteRule
                    {
                        Inbound = [SingboxTags.TunInbound],
                        PreferredBy = [SingboxTags.TailscaleEndpoint],
                        Action = RouteRuleAction.Route,
                        Outbound = SingboxTags.TailscaleEndpoint
                    }
                ] : []),
            new(RoutePhase.PreSniffDirect, RoutePhase.Tailscale,
            [
                new RouteRule { IpIsPrivate = true, Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                new RouteRule { IpCidr = ["223.5.5.5/32"], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound }
            ], DirectForwardingMode.PreSniff),
            new(RoutePhase.Sniff, RoutePhase.PreSniffDirect,
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
                    Timeout = RouteTuningPolicy.SniffTimeout
                },
                new RouteRule
                {
                    Inbound = [SingboxTags.TunInbound],
                    Network = ["udp"],
                    Action = RouteRuleAction.Sniff,
                    Sniffer = ["quic", "stun"],
                    Timeout = RouteTuningPolicy.SniffTimeout
                }
            ]),
            new(RoutePhase.BlockStunAndAds, RoutePhase.Sniff,
            [
                new RouteRule
                {
                    Inbound = [SingboxTags.TunInbound],
                    Protocol = ["stun"],
                    Network = ["udp"],
                    Action = RouteRuleAction.Reject
                },
                new RouteRule { RuleSet = [RuleSetTags.Ads], Action = RouteRuleAction.Reject }
            ]),
            new(RoutePhase.PriorityUdp443Reject, RoutePhase.BlockStunAndAds,
                prioritizedServices.Select(service => CreateUdp443RejectRule([.. service.RuleSets])).ToArray()),
            new(RoutePhase.LateIpv6Guard, RoutePhase.PriorityUdp443Reject,
                addressFamily == AddressFamilyPolicy.Ipv4Only ? [CreateIpv6RejectRule()] : []),
            new(RoutePhase.PriorityServices, RoutePhase.LateIpv6Guard,
                prioritizedServices.Select(CreateServiceRouteRule).ToArray()),
            new(RoutePhase.DomesticUdp443, RoutePhase.PriorityServices,
            [
                CreateDomesticUdp443DirectRule(RuleSetTags.Cn),
                CreateDomesticUdp443DirectRule(RuleSetTags.CnIp),
                CreateUdp443RejectRule()
            ], DirectForwardingMode.PostUdpSniff),
            new(RoutePhase.OtherServices, RoutePhase.DomesticUdp443,
                otherServices.Select(CreateServiceRouteRule).ToArray()),
            new(RoutePhase.DomesticDirect, RoutePhase.OtherServices,
            [
                new RouteRule { RuleSet = [RuleSetTags.Cn], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound },
                new RouteRule { RuleSet = [RuleSetTags.CnIp], Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound }
            ], DirectForwardingMode.PostUdpSniff),
            new(RoutePhase.FinalOutbound, RoutePhase.DomesticDirect, [])
        ];
    }

    internal static void ValidateRulePlan(IReadOnlyList<RuleStage> stages)
    {
        var completed = new HashSet<RoutePhase>();
        foreach (RuleStage stage in stages)
        {
            if (stage.After is { } predecessor && !completed.Contains(predecessor))
                throw new InvalidOperationException($"路由阶段 {stage.Phase} 必须位于 {predecessor} 之后。");
            if (!completed.Add(stage.Phase))
                throw new InvalidOperationException($"路由阶段 {stage.Phase} 重复。");
        }
        if (!completed.SetEquals(Enum.GetValues<RoutePhase>()))
            throw new InvalidOperationException("路由阶段声明不完整。");
    }

    private static IEnumerable<RouteRule> ExpandDirectForwarding(IReadOnlyList<RuleStage> stages, bool enabled)
    {
        foreach (RuleStage stage in stages)
        {
            foreach (RouteRule rule in stage.Rules)
            {
                if (enabled && stage.Forwarding is { } mode
                    && rule.Action == RouteRuleAction.Route && rule.Outbound == SingboxTags.DirectOutbound)
                {
                    yield return CreateForwardingRule(rule, mode, useBridge: false);
                    yield return CreateForwardingRule(rule, mode, useBridge: true);
                }
                yield return rule;
            }
        }
    }

    internal sealed record RuleStage(
        RoutePhase Phase,
        RoutePhase? After,
        IReadOnlyList<RouteRule> Rules,
        DirectForwardingMode? Forwarding = null);

    internal enum RoutePhase
    {
        DnsHijack,
        EarlyIpv6Guard,
        Tailscale,
        PreSniffDirect,
        Sniff,
        BlockStunAndAds,
        PriorityUdp443Reject,
        LateIpv6Guard,
        PriorityServices,
        DomesticUdp443,
        OtherServices,
        DomesticDirect,
        FinalOutbound
    }

    internal enum DirectForwardingMode
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
