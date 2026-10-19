using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders;

public interface ISingboxConfigBuilder
{
    SingboxConfig Build(SingboxBuildRequest request);
}

public sealed class SingboxConfigBuilder(
    TailscaleEndpointBuilder tailscaleEndpointBuilder,
    DnsProfileBuilder dnsProfileBuilder,
    RouteProfileBuilder routeProfileBuilder,
    SingboxApiServiceBuilder singboxApiServiceBuilder,
    BoxForgeProfile profile) : ISingboxConfigBuilder
{
    internal ConfigurationPlan Plan(SingboxBuildRequest request)
    {
        PlatformProfile platform = profile.GetPlatform(request.Platform);
        var groups = ProfilePlanner.PlanGroups(request.Nodes, profile, platform.UrlTestInterval);
        var dns = dnsProfileBuilder.Plan(request.Platform);
        var route = routeProfileBuilder.Plan(request.Platform);
        var endpoint = tailscaleEndpointBuilder.Plan(request.Platform);
        bool apiEnabled = singboxApiServiceBuilder.IsEnabled;
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
        var plan = new ConfigurationPlan(platform, request.Nodes.GetLeafTags().ToArray(),
            groups, dns, route, endpoint.Enabled, endpoint.TaildropDirectory, apiEnabled, clients);
        plan.Validate();
        return plan;
    }

    public SingboxConfig Build(SingboxBuildRequest request)
    {
        var plan = Plan(request);
        PlatformProfile platform = plan.Platform;
        var endpoints = TailscaleEndpointBuilder.Build(plan.TaildropDirectory, plan.TailscaleEndpointEnabled);
        var services = SingboxApiServiceBuilder.Build(plan.ApiServiceEnabled);
        var httpClients = plan.HttpClients.Select(client => new HttpClientConfig
        {
            Tag = client.Name,
            Detour = client.Detour,
            DomainResolver = ControlPlaneDnsPolicy.CreateResolver(platform.AddressFamily) with { Server = client.Resolver }
        }).ToList();
        var profiles = SingboxGroupEmitter.Emit(plan.Groups);
        List<Outbound> orderedOutbounds =
        [
            profiles.MainOutbound,
            .. profiles.RegionOutbounds,
            .. profiles.RegionAutoOutbounds,
            .. profiles.ServiceOutbounds,
            .. request.Nodes.Emit(platform)
        ];
        if (platform.UseDirectForwarding)
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
            Inbounds = InboundBuilder.Build(platform),
            Endpoints = endpoints.Count > 0 ? endpoints : null,
            Outbounds = orderedOutbounds,
            Route = SingboxRouteEmitter.Emit(plan.Route),
            Services = services.Count > 0 ? services : null,
            Experimental = ExperimentalBuilder.Build(request.CacheId)
        };
    }
}
