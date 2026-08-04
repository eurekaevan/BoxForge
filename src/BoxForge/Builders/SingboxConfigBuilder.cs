using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

public interface ISingboxConfigBuilder
{
    SingboxConfig Build(SingboxBuildRequest request);
}

public class SingboxConfigBuilder(
    ProfilePlanner profilePlanner,
    TailscaleEndpointBuilder tailscaleEndpointBuilder,
    DnsProfileBuilder dnsProfileBuilder,
    RouteProfileBuilder routeProfileBuilder,
    ServiceBuilder serviceBuilder) : ISingboxConfigBuilder
{
    public SingboxConfig Build(SingboxBuildRequest request)
    {
        var profiles = profilePlanner.Plan(request.Nodes);
        var endpoints = tailscaleEndpointBuilder.Build();

        var orderedOutbounds = new List<Outbound>();
        orderedOutbounds.Add(profiles.MainOutbound);
        orderedOutbounds.AddRange(profiles.RegionOutbounds);
        orderedOutbounds.AddRange(profiles.ServiceOutbounds);
        orderedOutbounds.AddRange(request.Nodes.Outbounds.Select(
            outbound => AddPlatformDialFields(outbound, request.Platform)));
        orderedOutbounds.Add(profiles.DirectOutbound);

        return new SingboxConfig
        {
            Log = new LogConfig(),
            Dns = dnsProfileBuilder.Build(request.Nodes),
            HttpClients =
            [
                new HttpClientConfig
                {
                    Tag = SingboxOptions.RuleSetHttpClientTag,
                    Detour = profiles.DirectOutbound.Tag
                }
            ],
            Inbounds = InboundBuilder.Build(request.Platform),
            Endpoints = endpoints.Count > 0 ? endpoints : null,
            Outbounds = orderedOutbounds,
            Route = routeProfileBuilder.Build(),
            Services = serviceBuilder.Build(
                request.Platform,
                request.GeneratedApiSecret) is { Count: > 0 } services
                ? services
                : null,
            Experimental = ExperimentalBuilder.Build(request.CacheId)
        };
    }

    private static ProxyOutbound AddPlatformDialFields(
        ProxyOutbound outbound,
        TargetPlatform platform) =>
        platform == TargetPlatform.Android
            ? outbound
            : outbound with
            {
                TcpKeepAlive = "1m",
                TcpKeepAliveInterval = "30s"
            };
}
