using BoxForge.Configuration;
using BoxForge.Helpers;
using BoxForge.Models;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal sealed class RouteProfileBuilder(BoxForgeProfile profile)
{
    internal RoutePlan Plan(TargetPlatform platform, BoxForgeProfile? effectiveProfile = null)
    {
        BoxForgeProfile settingsProfile = (effectiveProfile ?? profile).ForPlatform(platform);
        PlatformProfile settings = settingsProfile.GetPlatform(platform);
        var ruleSets = settingsProfile.RuleSets.Select(source =>
        {
            if (!GoDurationHelper.TryNormalizePositiveExtendedDuration(source.UpdateInterval, out string interval))
                throw new InvalidOperationException("已校验的规则源时长无效。");
            string url = source.Tags.Length == 1
                ? source.Url.Replace("{tag}", source.Tags[0], StringComparison.Ordinal) : source.Url;
            return new RuleSetPlan(source.Tags, url, interval);
        }).ToArray();
        var plan = new RoutePlan(
            settings.AddressFamily,
            settingsProfile.Features.Tailscale.Enabled,
            settings.UseDirectForwarding,
            settingsProfile.GetResolver("alidns").Server, settingsProfile.Route.SniffTimeout,
            SingboxTags.MainProxyGroup, ruleSets,
            settingsProfile.Services.Where(service => service.PrecedesDomesticRoutes).ToArray(),
            settingsProfile.Services.Where(service => !service.PrecedesDomesticRoutes).ToArray(),
            Enum.GetValues<RoutePhase>(), settingsProfile.Route.SniffExcludedPorts);
        plan.Validate();
        return plan;
    }
}
