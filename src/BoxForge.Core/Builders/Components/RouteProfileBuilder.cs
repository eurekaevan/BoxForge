using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal sealed class RouteProfileBuilder(BoxForgeProfile profile)
{
    internal RoutePlan Plan(TargetPlatform platform, BoxForgeProfile? effectiveProfile = null)
    {
        BoxForgeProfile settingsProfile = effectiveProfile ?? profile;
        PlatformProfile settings = settingsProfile.GetPlatform(platform);
        var plan = new RoutePlan(
            settings.AddressFamily,
            settingsProfile.Runtime.Tailscale.IsEnabled(settings),
            settings.UseDirectForwarding,
            settingsProfile.GetResolver("alidns").Server, settingsProfile.Policies.SniffTimeout,
            SingboxTags.MainProxyGroup, settingsProfile.RuleSets.Select(source =>
                new RuleSetPlan(source.Tags, source.Url, source.UpdateInterval)).ToArray(),
            settingsProfile.Services.Where(service => service.PrecedesDomesticRoutes).ToArray(),
            settingsProfile.Services.Where(service => !service.PrecedesDomesticRoutes).ToArray(),
            Enum.GetValues<RoutePhase>(), settingsProfile.Policies.SniffExcludedPorts);
        plan.Validate();
        return plan;
    }
}
