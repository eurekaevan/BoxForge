using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders;

internal interface ISingboxConfigBuilder
{
    SingboxConfig Build(ConfigurationPlan plan, string? cacheId);
}

internal sealed class SingboxConfigBuilder : ISingboxConfigBuilder
{
    public SingboxConfig Build(ConfigurationPlan plan, string? cacheId)
    {
        plan.Validate();
        var endpoints = TailscaleEndpointBuilder.Emit(plan.TaildropDirectory, plan.TailscaleEndpointEnabled);
        var services = SingboxApiServiceBuilder.Emit(plan.ApiServiceEnabled);
        var httpClients = plan.HttpClients.Select(client => new HttpClientConfig
        {
            Tag = client.Name,
            Detour = client.Detour,
            DomainResolver = SingboxResolverEmitter.Emit(client.Resolver)
        }).ToList();
        var profiles = SingboxGroupEmitter.Emit(plan.Groups);
        List<Outbound> orderedOutbounds =
        [
            profiles.MainOutbound,
            .. profiles.RegionOutbounds,
            .. profiles.RegionAutoOutbounds,
            .. profiles.ServiceOutbounds,
            .. plan.Nodes.Select(node => SingboxNodeEmitter.Emit(node) with
            {
                DomainResolver = plan.NodeResolvers.TryGetValue(node.Name, out var resolver)
                    ? SingboxResolverEmitter.Emit(resolver) : null
            })
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
            Experimental = ExperimentalBuilder.Build(cacheId)
        };
    }
}
