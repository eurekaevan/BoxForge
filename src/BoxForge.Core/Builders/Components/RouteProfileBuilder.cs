using BoxForge.Configuration;
using BoxForge.Helpers;
using BoxForge.Models;
using System.Collections.Immutable;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal static class RouteProfileBuilder
{
    internal static RoutePlan Plan(ResolvedProfile settingsProfile)
    {
        PlatformProfile settings = settingsProfile.Settings;
        var ruleSets = settingsProfile.RuleSets.Select(source =>
        {
            if (!GoDurationHelper.TryNormalizePositiveExtendedDuration(source.UpdateInterval, out string interval))
                throw new InvalidOperationException("已校验的规则源时长无效。");
            string url = source.Tags.Length == 1
                ? source.Url.Replace("{tag}", source.Tags[0], StringComparison.Ordinal) : source.Url;
            return new RuleSetPlan(source.Tags, url, interval);
        }).ToArray();
        var rules = ImmutableArray.CreateBuilder<RouteRulePlan>();
        foreach (RoutePhase phase in Enum.GetValues<RoutePhase>())
        {
            DirectForwardingMode? forwarding = phase switch
            {
                RoutePhase.PreSniffDirect => DirectForwardingMode.PreSniff,
                RoutePhase.DomesticUdp443 or RoutePhase.DomesticDirect => DirectForwardingMode.PostUdpSniff,
                _ => null
            };
            foreach (RouteRulePlan rule in CreateRules(phase, settingsProfile, settings))
            {
                if (settings.UseDirectForwarding && forwarding is { } mode
                    && rule.Action == RouteOperation.Route && rule.Outbound == SingboxTags.DirectOutbound)
                {
                    rules.Add(CreateForwardingRule(rule, mode, useBridge: false));
                    rules.Add(CreateForwardingRule(rule, mode, useBridge: true));
                }
                rules.Add(rule);
            }
        }
        return new RoutePlan(settings.AddressFamily, settingsProfile.Features.Tailscale.Enabled,
            settings.UseDirectForwarding, SingboxTags.MainProxyGroup, HttpClientTags.RuleSetProxy,
            new DomainResolverPlan(SingboxTags.DirectAliDns, settings.AddressFamily, true), ruleSets,
            Enum.GetValues<RoutePhase>(), rules.ToImmutable());
    }
    private static RouteRulePlan[] CreateRules(RoutePhase phase, ResolvedProfile profile, PlatformProfile settings) => phase switch
    {
        RoutePhase.DnsHijack =>
        [
            new RouteRulePlan(phase)
            {
                Mode = LogicalMatchMode.And,
                Rules =
                [
                    new RouteRulePlan(phase) { Inbound = [SingboxTags.TunInbound] },
                    new RouteRulePlan(phase)
                    {
                        Mode = LogicalMatchMode.Or,
                        Rules = [new RouteRulePlan(phase) { Protocol = ["dns"] }, new RouteRulePlan(phase) { Port = [53] }]
                    }
                ],
                Action = RouteOperation.HijackDns
            }
        ],
        RoutePhase.EarlyIpv6Guard => settings.AddressFamily != AddressFamilyPolicy.DualStack ? [CreateIpv6RejectRule(settings.AddressFamily, phase)] : [],
        RoutePhase.Tailscale => profile.Features.Tailscale.Enabled ?
            [
                new RouteRulePlan(phase)
                {
                    Inbound = [SingboxTags.TunInbound],
                    PreferredBy = [SingboxTags.TailscaleEndpoint],
                    Action = RouteOperation.Route,
                    Outbound = SingboxTags.TailscaleEndpoint
                }
            ] : [],
        RoutePhase.PreSniffDirect =>
        [
            new RouteRulePlan(phase) { IpIsPrivate = true, Action = RouteOperation.Route, Outbound = SingboxTags.DirectOutbound },
            new RouteRulePlan(phase) { IpCidr = [$"{profile.GetResolver("alidns").Server}/32"], Action = RouteOperation.Route, Outbound = SingboxTags.DirectOutbound }
        ],
        RoutePhase.Sniff =>
        [
            new RouteRulePlan(phase)
            {
                Mode = LogicalMatchMode.And,
                Rules =
                [
                    new RouteRulePlan(phase) { Inbound = [SingboxTags.TunInbound], Network = ["tcp"] },
                    .. profile.Route.SniffExcludedPorts.IsEmpty
                        ? Array.Empty<RouteRulePlan>()
                        : [new RouteRulePlan(phase) { Port = profile.Route.SniffExcludedPorts, Invert = true }]
                ],
                Action = RouteOperation.Sniff,
                Sniffer = ["http", "tls"],
                Timeout = profile.Route.SniffTimeout
            },
            new RouteRulePlan(phase)
            {
                Inbound = [SingboxTags.TunInbound],
                Network = ["udp"],
                Action = RouteOperation.Sniff,
                Sniffer = ["quic", "stun"],
                Timeout = profile.Route.SniffTimeout
            }
        ],
        RoutePhase.BlockStunAndAds =>
        [
            new RouteRulePlan(phase)
            {
                Inbound = [SingboxTags.TunInbound],
                Protocol = ["stun"],
                Network = ["udp"],
                Action = RouteOperation.Reject
            },
            new RouteRulePlan(phase) { RuleSet = [RuleSetTags.Ads], Action = RouteOperation.Reject }
        ],
        RoutePhase.LateIpv6Guard => settings.AddressFamily != AddressFamilyPolicy.DualStack ? [CreateIpv6RejectRule(settings.AddressFamily, phase)] : [],
        RoutePhase.PriorityServices => profile.Services.Where(service => service.PrecedesDomesticRoutes).Select(service => CreateServiceRouteRule(service, phase)).ToArray(),
        RoutePhase.DomesticUdp443 =>
        [
            CreateDomesticUdp443DirectRule(RuleSetTags.Cn, phase),
            CreateDomesticUdp443DirectRule(RuleSetTags.CnIp, phase)
        ],
        RoutePhase.OtherServices => profile.Services.Where(service => !service.PrecedesDomesticRoutes).Select(service => CreateServiceRouteRule(service, phase)).ToArray(),
        RoutePhase.DomesticDirect =>
        [
            new RouteRulePlan(phase) { RuleSet = [RuleSetTags.Cn], Action = RouteOperation.Route, Outbound = SingboxTags.DirectOutbound },
            new RouteRulePlan(phase) { RuleSet = [RuleSetTags.CnIp], Action = RouteOperation.Route, Outbound = SingboxTags.DirectOutbound }
        ],
        RoutePhase.FinalOutbound => [],
        _ => throw new InvalidOperationException("Unknown route phase.")
    };

    private enum DirectForwardingMode
    {
        PreSniff,
        PostUdpSniff
    }

    private static RouteRulePlan CreateForwardingRule(
        RouteRulePlan directRule,
        DirectForwardingMode forwardingMode,
        bool useBridge) => directRule with
        {
            Inbound = [SingboxTags.TunInbound],
            Network = forwardingMode == DirectForwardingMode.PostUdpSniff ? ["udp"] : directRule.Network,
            PreferredBy = useBridge ? [SingboxTags.BridgeOutbound] : default,
            Action = useBridge ? RouteOperation.Route : RouteOperation.Bypass,
            Outbound = useBridge ? SingboxTags.BridgeOutbound : null
        };

    private static RouteRulePlan CreateIpv6RejectRule(AddressFamilyPolicy addressFamily, RoutePhase phase) =>
        addressFamily == AddressFamilyPolicy.Auto ? new RouteRulePlan(phase)
        {
            Mode = LogicalMatchMode.And,
            Rules =
            [
                new RouteRulePlan(phase) { IpVersion = 6 },
                new RouteRulePlan(phase) { DefaultInterfaceAddress = [AddressFamilyPolicies.GlobalIpv6Prefix], Invert = true }
            ],
            Action = RouteOperation.Reject
        } : new RouteRulePlan(phase)
        {
            IpVersion = 6,
            Action = RouteOperation.Reject
        };

    private static RouteRulePlan CreateDomesticUdp443DirectRule(string ruleSet, RoutePhase phase) =>
        new(phase)
        {
            Inbound = [SingboxTags.TunInbound],
            Port = [443],
            Network = ["udp"],
            RuleSet = [ruleSet],
            Action = RouteOperation.Route,
            Outbound = SingboxTags.DirectOutbound
        };

    private static RouteRulePlan CreateServiceRouteRule(ServiceDefinition service, RoutePhase phase) =>
        new(phase)
        {
            RuleSet = [.. service.RuleSets],
            Action = RouteOperation.Route,
            Outbound = service.Name
        };
}
