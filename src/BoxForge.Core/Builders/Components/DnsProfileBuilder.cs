using System.Collections.Immutable;
using BoxForge.Configuration;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal static class DnsProfileBuilder
{
    internal static DnsPlan Plan(ResolvedProfile settingsProfile)
    {
        PlatformProfile settings = settingsProfile.Settings;
        List<DnsHttpsResolverPlan> resolvers =
        [
            CreateResolver(SingboxTags.DirectAliDns, settingsProfile.GetResolver("alidns")),
            CreateResolver(SingboxTags.DirectTencentDns, settingsProfile.GetResolver("tencent")),
            CreateResolver(SingboxTags.ProxyCloudflareDns, settingsProfile.GetResolver("cloudflare"), SingboxTags.MainProxyGroup),
            CreateResolver(SingboxTags.ProxyGoogleDns, settingsProfile.GetResolver("google"), SingboxTags.MainProxyGroup)
        ];
        var pools = ImmutableArray.CreateBuilder<DnsResolverPoolPlan>();
        foreach (var service in settingsProfile.Services.OrderByDescending(service => service.PrecedesDomesticRoutes))
        {
            string primary = SingboxTags.ServiceDns(service.Id, "cloudflare");
            string secondary = SingboxTags.ServiceDns(service.Id, "google");
            resolvers.Add(CreateResolver(primary, settingsProfile.GetResolver("cloudflare"), service.Name));
            resolvers.Add(CreateResolver(secondary, settingsProfile.GetResolver("google"), service.Name));
            pools.Add(new DnsResolverPoolPlan(DnsPoolKind.Service, service.RuleSets, primary, secondary, service.Id));
        }
        pools.Add(new DnsResolverPoolPlan(DnsPoolKind.Domestic, [RuleSetTags.Cn],
            SingboxTags.DirectAliDns, SingboxTags.DirectTencentDns));
        pools.Add(new DnsResolverPoolPlan(DnsPoolKind.Global, default,
            SingboxTags.ProxyCloudflareDns, SingboxTags.ProxyGoogleDns));
        bool tailscaleEnabled = settingsProfile.Features.Tailscale.Enabled;
        return new DnsPlan(settings.AddressFamily, settings.DnsSelectionMode,
            resolvers, tailscaleEnabled,
            SingboxTags.ProxyCloudflareDns,
            settingsProfile.Dns.Timeout, settingsProfile.Dns.CacheCapacity,
            settingsProfile.Dns.OptimisticTimeout)
        {
            Pools = pools.ToImmutable()
        };
    }

    private static DnsHttpsResolverPlan CreateResolver(
        string name, BuiltInProfile.Resolver resolver, string? detour = null) =>
        new(name, resolver.Server, resolver.ServerName, detour);
}
