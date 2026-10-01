using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Configuration;

public static class ControlPlaneDnsPolicy
{
    public static DnsResolverOptions CreateResolver(TargetPlatform platform) =>
        CreateResolver(AddressFamilyPolicies.For(platform));

    public static DnsResolverOptions CreateResolver(AddressFamilyPolicy addressFamily) => new()
    {
        Server = SingboxTags.DirectAliDns,
        Strategy = addressFamily.ToDnsStrategy(),
        DisableOptimisticCache = true
    };
}
