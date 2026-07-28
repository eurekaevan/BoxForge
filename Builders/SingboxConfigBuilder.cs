using SubConvert.Builders.Components;
using SubConvert.Models;
using SubConvert.Models.Singbox;

namespace SubConvert.Builders;

public interface ISingboxConfigBuilder
{
    SingboxConfig Build(SingboxBuildRequest request);
}

public class SingboxConfigBuilder(
    NodeCatalogBuilder nodeCatalogBuilder,
    ProfilePlanner profilePlanner,
    InboundBuilder inboundBuilder,
    TailscaleEndpointBuilder tailscaleEndpointBuilder,
    DnsProfileBuilder dnsProfileBuilder,
    RouteProfileBuilder routeProfileBuilder,
    ExperimentalBuilder experimentalBuilder) : ISingboxConfigBuilder
{
    public SingboxConfig Build(SingboxBuildRequest request)
    {
        var nodes = nodeCatalogBuilder.Build(request.ClashConfig);
        var profiles = profilePlanner.Plan(nodes);
        var endpoints = tailscaleEndpointBuilder.Build();

        var orderedOutbounds = new List<Outbound>();
        orderedOutbounds.Add(profiles.MainOutbound);
        orderedOutbounds.AddRange(profiles.RegionOutbounds);
        orderedOutbounds.AddRange(profiles.ServiceOutbounds);
        orderedOutbounds.AddRange(nodes.Outbounds);
        orderedOutbounds.Add(profiles.DirectOutbound);

        return new SingboxConfig
        {
            Log = new LogConfig(),
            Dns = dnsProfileBuilder.Build(nodes),
            Inbounds = inboundBuilder.Build(request.Platform),
            Endpoints = endpoints.Count > 0 ? endpoints : null,
            Outbounds = orderedOutbounds,
            Route = routeProfileBuilder.Build(),
            Experimental = experimentalBuilder.Build(
                request.Platform,
                request.CacheId)
        };
    }
}
