using BoxForge.Configuration;
using BoxForge.Helpers;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal static class RouteProfileBuilder
{
    internal static RoutePlan Plan(ResolvedProfile settingsProfile)
    {
        PlatformProfile settings = settingsProfile.Settings;
        var ruleSets = settingsProfile.RuleSets.Select(source =>
        {
            if (!GoDurationHelper.TryNormalizePositiveExtendedDuration(source.UpdateInterval, out string interval))
                throw new InvalidOperationException("已校验的规则源时长无效。");
            string url = source.Tags.Length == 1
                ? source.Url.Replace("{tag}", source.Tags[0], StringComparison.Ordinal) : source.Url;
            return new RuleSetPlan(source.Tags, url, interval);
        }).ToArray();
        return new RoutePlan(settings.AddressFamily, settingsProfile.Features.Tailscale.Enabled,
            settings.UseDirectForwarding, SingboxTags.MainProxyGroup, HttpClientTags.RuleSetProxy,
            new DomainResolverPlan(SingboxTags.DirectAliDns, settings.AddressFamily.ForControlPlane(), true), ruleSets,
            settingsProfile.GetResolver("alidns").Server, settingsProfile.Route.SniffTimeout,
            settingsProfile.Route.SniffExcludedPorts,
            [.. settingsProfile.Services.OrderByDescending(service => service.PrecedesDomesticRoutes)
                .Select(service => new ServiceRoutePlan(service.Id, service.RuleSets, service.Name, service.PrecedesDomesticRoutes))]);
    }
}
