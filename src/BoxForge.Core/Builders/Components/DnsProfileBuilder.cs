using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed class DnsProfileBuilder(IOptions<TailscaleOptions> tailscaleOptions, BoxForgeProfile profile)
{
    private readonly TailscaleOptions tailscale = tailscaleOptions.Value;

    public DnsConfig Build(TargetPlatform platform) => SingboxDnsEmitter.Emit(Plan(platform));

    internal DnsPlan Plan(TargetPlatform platform)
    {
        PlatformProfile settings = profile.GetPlatform(platform);
        DnsHttpsResolverPlan[] resolvers =
        [
            CreateResolver(SingboxTags.DirectAliDns, profile.GetResolver("alidns")),
            CreateResolver(SingboxTags.DirectTencentDns, profile.GetResolver("tencent")),
            CreateResolver(SingboxTags.ProxyCloudflareDns, profile.GetResolver("cloudflare"), SingboxTags.MainProxyGroup),
            CreateResolver(SingboxTags.ProxyGoogleDns, profile.GetResolver("google"), SingboxTags.MainProxyGroup)
        ];
        string[] priorityRuleSets = profile.Services
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
            resolvers, tailscale.IsEnabled(settings), [RuleSetTags.Ads], pools,
            SingboxTags.ProxyCloudflareDns);
        plan.Validate();
        return plan;
    }

    private static DnsHttpsResolverPlan CreateResolver(
        string name, BuiltInProfile.Resolver resolver, string? detour = null) =>
        new(name, resolver.Server, resolver.ServerName, detour);
}
