using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal sealed class DnsProfileBuilder(BoxForgeProfile profile)
{
    internal DnsPlan Plan(TargetPlatform platform, BoxForgeProfile? effectiveProfile = null)
    {
        BoxForgeProfile settingsProfile = (effectiveProfile ?? profile).ForPlatform(platform);
        PlatformProfile settings = settingsProfile.GetPlatform(platform);
        DnsHttpsResolverPlan[] resolvers =
        [
            CreateResolver(SingboxTags.DirectAliDns, settingsProfile.GetResolver("alidns")),
            CreateResolver(SingboxTags.DirectTencentDns, settingsProfile.GetResolver("tencent")),
            CreateResolver(SingboxTags.ProxyCloudflareDns, settingsProfile.GetResolver("cloudflare"), SingboxTags.MainProxyGroup),
            CreateResolver(SingboxTags.ProxyGoogleDns, settingsProfile.GetResolver("google"), SingboxTags.MainProxyGroup)
        ];
        string[] priorityRuleSets = settingsProfile.Services
            .SelectMany(service => service.RuleSets)
            .Distinct(StringComparer.Ordinal).ToArray();
        var pools = new List<DnsResolverPoolPlan>();
        if (priorityRuleSets.Length > 0)
        {
            pools.Add(new DnsResolverPoolPlan("priority", priorityRuleSets,
                SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns));
        }
        pools.Add(new DnsResolverPoolPlan("domestic", [RuleSetTags.Cn],
            SingboxTags.DirectAliDns, SingboxTags.DirectTencentDns));
        pools.Add(new DnsResolverPoolPlan("global", null,
            SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns));
        var plan = new DnsPlan(settings.AddressFamily, settings.DnsSelectionMode,
            resolvers, settingsProfile.Features.Tailscale.Enabled,
            [RuleSetTags.Ads], pools,
            SingboxTags.ProxyCloudflareDns,
            settingsProfile.Dns.Timeout, settingsProfile.Dns.CacheCapacity,
            settingsProfile.Dns.OptimisticTimeout);
        plan.Validate();
        return plan;
    }

    private static DnsHttpsResolverPlan CreateResolver(
        string name, BuiltInProfile.Resolver resolver, string? detour = null) =>
        new(name, resolver.Server, resolver.ServerName, detour);
}
