using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

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
    ServiceBuilder serviceBuilder,
    ExperimentalBuilder experimentalBuilder) : ISingboxConfigBuilder
{
    public SingboxConfig Build(SingboxBuildRequest request)
    {
        var nodes = nodeCatalogBuilder.Build(
            request.ClashConfig,
            request.StrictNodeValidation);
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
            HttpClients =
            [
                new HttpClientConfig
                {
                    Tag = SingboxOptions.RuleSetHttpClientTag,
                    Detour = profiles.DirectOutbound.Tag
                }
            ],
            Inbounds = inboundBuilder.Build(request.Platform),
            Endpoints = endpoints.Count > 0 ? endpoints : null,
            Outbounds = orderedOutbounds,
            Route = routeProfileBuilder.Build(),
            Services = serviceBuilder.Build(request.Platform) is { Count: > 0 } services
                ? services
                : null,
            Experimental = experimentalBuilder.Build(request.CacheId)
        };
    }
}
