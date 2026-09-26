using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Configuration;

public static class ControlPlaneDnsPolicy
{
    public static DnsResolverOptions CreateResolver(TargetPlatform platform) => new()
    {
        Server = SingboxTags.DirectAliDns,
        Strategy = AddressFamilyPolicies.For(platform).ToDnsStrategy(),
        DisableOptimisticCache = true
    };
}
