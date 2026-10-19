using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed class RouteProfileBuilder(
    IOptions<TailscaleOptions> tailscaleOptions,
    BoxForgeProfile profile)
{
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public RouteConfig Build(TargetPlatform platform) => SingboxRouteEmitter.Emit(Plan(platform));

    internal RoutePlan Plan(TargetPlatform platform)
    {
        PlatformProfile settings = profile.GetPlatform(platform);
        var plan = new RoutePlan(
            settings.AddressFamily, tailscale.IsEnabled(settings), settings.UseDirectForwarding,
            profile.GetResolver("alidns").Server, RouteTuningPolicy.SniffTimeout,
            SingboxTags.MainProxyGroup, profile.RuleSets,
            profile.Services.Where(service => service.PrecedesDomesticRoutes).ToArray(),
            profile.Services.Where(service => !service.PrecedesDomesticRoutes).ToArray(),
            Enum.GetValues<RoutePhase>());
        plan.Validate();
        return plan;
    }
}
