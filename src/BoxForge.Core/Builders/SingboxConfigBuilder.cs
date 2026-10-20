using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders;

internal interface ISingboxConfigBuilder
{
    SingboxConfig Build(SingboxBuildRequest request);
}

internal sealed class SingboxConfigBuilder(
    TailscaleEndpointBuilder tailscaleEndpointBuilder,
    DnsProfileBuilder dnsProfileBuilder,
    RouteProfileBuilder routeProfileBuilder,
    BoxForgeProfile profile) : ISingboxConfigBuilder
{
    internal ConfigurationPlan Plan(SingboxBuildRequest request)
    {
        BoxForgeProfile settings = request.Profile ?? profile;
        PlatformProfile platform = settings.GetPlatform(request.Platform);
        var groups = ProfilePlanner.PlanGroups(request.Nodes, settings, platform.UrlTestInterval);
        var dns = dnsProfileBuilder.Plan(request.Platform, request.Profile);
        var route = routeProfileBuilder.Plan(request.Platform, request.Profile);
        var endpoint = tailscaleEndpointBuilder.Plan(request.Platform, request.Profile);
        bool apiEnabled = settings.Runtime.SingboxApi.Enabled;
        string ruleSetDetour = groups.Probes.Count > 0
            ? groups.Probes[0].Name
            : request.Nodes.Names.Count > 0
                ? request.Nodes.Names[0]
                : SingboxTags.MainProxyGroup;
        var clients = new List<HttpClientPlan>
        {
            new(HttpClientTags.RuleSetProxy, ruleSetDetour, SingboxTags.DirectAliDns)
        };
        if (apiEnabled)
            clients.Add(new HttpClientPlan(HttpClientTags.DashboardDirect, null, SingboxTags.DirectAliDns));
        var plan = new ConfigurationPlan(platform.AddressFamily, platform.DnsSelectionMode, platform.UseDirectForwarding, platform.UrlTestInterval,
            new TunPlan(platform.TunAddresses, platform.AutoRedirect, platform.MultiQueue, platform.StrictRoute),
            request.Nodes.Nodes.Select(node => OutboundTuningPolicy.Apply(node, platform)).ToArray(),
            groups, dns, route, endpoint.Enabled, endpoint.TaildropDirectory, apiEnabled, clients);
        plan.Validate();
        return plan;
    }

    public SingboxConfig Build(SingboxBuildRequest request)
    {
        var plan = Plan(request);
        var endpoints = TailscaleEndpointBuilder.Emit(plan.TaildropDirectory, plan.TailscaleEndpointEnabled);
        var services = SingboxApiServiceBuilder.Emit(plan.ApiServiceEnabled);
        var httpClients = plan.HttpClients.Select(client => new HttpClientConfig
        {
            Tag = client.Name,
            Detour = client.Detour,
            DomainResolver = ControlPlaneDnsPolicy.CreateResolver(plan.AddressFamily) with { Server = client.Resolver }
        }).ToList();
        var profiles = SingboxGroupEmitter.Emit(plan.Groups);
        List<Outbound> orderedOutbounds =
        [
            profiles.MainOutbound,
            .. profiles.RegionOutbounds,
            .. profiles.RegionAutoOutbounds,
            .. profiles.ServiceOutbounds,
            .. plan.Nodes.Select(SingboxNodeEmitter.Emit)
        ];
        if (plan.UseDirectForwarding)
        {
            orderedOutbounds.Add(new BridgeOutbound
            {
                Tag = SingboxTags.BridgeOutbound
            });
        }
        orderedOutbounds.Add(profiles.DirectOutbound);

        return new SingboxConfig
        {
            Log = new LogConfig(),
            Dns = SingboxDnsEmitter.Emit(plan.Dns),
            HttpClients = httpClients,
            Inbounds = InboundBuilder.Emit(plan.Tun),
            Endpoints = endpoints.Count > 0 ? endpoints : null,
            Outbounds = orderedOutbounds,
            Route = SingboxRouteEmitter.Emit(plan.Route),
            Services = services.Count > 0 ? services : null,
            Experimental = ExperimentalBuilder.Build(request.CacheId)
        };
    }
}
