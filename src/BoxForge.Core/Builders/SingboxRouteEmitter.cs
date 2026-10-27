using BoxForge.Configuration;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxRouteEmitter
{
    internal static RouteConfig Emit(RoutePlan plan)
    {
        var route = new RouteConfig
        {
            AutoDetectInterface = true,
            Final = plan.FinalOutbound,
            DefaultHttpClient = plan.DefaultHttpClient,
            DefaultDomainResolver = SingboxResolverEmitter.Emit(plan.DefaultDomainResolver)
        };
        route.RuleSet.AddRange(plan.RuleSets.Select(source => new SingboxRuleSet
        {
            Tag = [.. source.Tags],
            Type = RuleSetType.Remote,
            Format = RuleSetFormat.Binary,
            Url = source.Url,
            UpdateInterval = source.UpdateInterval
        }));
        List<RouteRule> rules = route.Rules;
        // TUN handles DNS natively; retain an explicit port 53 fallback for TCP and UDP.
        rules.Add(new RouteRule { Inbound = [SingboxTags.TunInbound], Port = [53], Action = RouteRuleAction.HijackDns });
        AddIpv6Guard();
        if (plan.TailscaleEnabled)
            rules.Add(new RouteRule
            {
                Inbound = [SingboxTags.TunInbound],
                PreferredBy = [SingboxTags.TailscaleEndpoint],
                Action = RouteRuleAction.Route,
                Outbound = SingboxTags.TailscaleEndpoint
            });
        AddDirect(new RouteRule { IpIsPrivate = true }, postSniff: false);
        AddDirect(new RouteRule { IpCidr = [$"{plan.BootstrapAddress}/32"] }, postSniff: false);
        rules.Add(new RouteRule
        {
            Type = RouteRuleType.Logical,
            Mode = RouteLogicalMode.And,
            Rules =
            [
                new RouteRule { Inbound = [SingboxTags.TunInbound], Network = ["tcp"] },
                .. plan.SniffExcludedPorts.IsEmpty ? Array.Empty<RouteRule>()
                    : [new RouteRule { Port = [.. plan.SniffExcludedPorts], Invert = true }]
            ],
            Action = RouteRuleAction.Sniff,
            Sniffer = ["http", "tls"],
            Timeout = plan.SniffTimeout
        });
        rules.Add(new RouteRule
        {
            Inbound = [SingboxTags.TunInbound],
            Network = ["udp"],
            Action = RouteRuleAction.Sniff,
            Sniffer = ["quic", "stun"],
            Timeout = plan.SniffTimeout
        });
        rules.Add(new RouteRule
        {
            Inbound = [SingboxTags.TunInbound],
            Protocol = ["stun"],
            Network = ["udp"],
            Action = RouteRuleAction.Reject
        });
        rules.Add(new RouteRule { RuleSet = [RuleSetTags.Ads], Action = RouteRuleAction.Reject });
        AddIpv6Guard();
        AddServices(priority: true);
        foreach (string scope in new[] { RuleSetTags.Cn, RuleSetTags.CnIp })
            AddDirect(new RouteRule { Inbound = [SingboxTags.TunInbound], Port = [443], Network = ["udp"], RuleSet = [scope] }, postSniff: true);
        AddServices(priority: false);
        foreach (string scope in new[] { RuleSetTags.Cn, RuleSetTags.CnIp })
            AddDirect(new RouteRule { RuleSet = [scope] }, postSniff: true);
        if (plan.AddressFamily == AddressFamilyPolicy.DirectDualStack)
            rules.Add(new RouteRule { IpVersion = 6, Action = RouteRuleAction.Reject });
        return route;

        void AddIpv6Guard()
        {
            if (plan.AddressFamily == AddressFamilyPolicy.DualStack) return;
            rules.Add(plan.AddressFamily.UsesInterfaceGuard() ? new RouteRule
            {
                Type = RouteRuleType.Logical,
                Mode = RouteLogicalMode.And,
                Rules =
                [
                    new RouteRule { IpVersion = 6 },
                    new RouteRule { DefaultInterfaceAddress = [AddressFamilyPolicies.GlobalIpv6Prefix], Invert = true }
                ],
                Action = RouteRuleAction.Reject
            } : new RouteRule { IpVersion = 6, Action = RouteRuleAction.Reject });
        }

        void AddServices(bool priority)
        {
            ServiceRoutePlan[] services = [.. plan.Services.Where(service => service.PrecedesDomesticRoutes == priority)];
            if (plan.AddressFamily == AddressFamilyPolicy.DirectDualStack && services.Length > 0)
                rules.Add(new RouteRule
                {
                    IpVersion = 6,
                    RuleSet = [.. services.SelectMany(service => service.RuleSets).Distinct(StringComparer.Ordinal)],
                    Action = RouteRuleAction.Reject
                });
            foreach (ServiceRoutePlan service in services)
                rules.Add(new RouteRule { RuleSet = [.. service.RuleSets], Action = RouteRuleAction.Route, Outbound = service.Outbound });
        }

        void AddDirect(RouteRule matcher, bool postSniff)
        {
            // Each emitted rule owns its matcher collections, including forwarding variants.
            if (plan.UseDirectForwarding)
            {
                rules.Add(matcher with
                {
                    IpCidr = matcher.IpCidr is null ? null : [.. matcher.IpCidr],
                    RuleSet = matcher.RuleSet is null ? null : [.. matcher.RuleSet],
                    Port = matcher.Port is null ? null : [.. matcher.Port],
                    Inbound = [SingboxTags.TunInbound],
                    Network = postSniff ? ["udp"] : null,
                    Action = RouteRuleAction.Bypass
                });
                rules.Add(matcher with
                {
                    IpCidr = matcher.IpCidr is null ? null : [.. matcher.IpCidr],
                    RuleSet = matcher.RuleSet is null ? null : [.. matcher.RuleSet],
                    Port = matcher.Port is null ? null : [.. matcher.Port],
                    Inbound = [SingboxTags.TunInbound],
                    Network = postSniff ? ["udp"] : null,
                    PreferredBy = [SingboxTags.BridgeOutbound],
                    Action = RouteRuleAction.Route,
                    Outbound = SingboxTags.BridgeOutbound
                });
            }
            rules.Add(matcher with { Action = RouteRuleAction.Route, Outbound = SingboxTags.DirectOutbound });
        }
    }
}
