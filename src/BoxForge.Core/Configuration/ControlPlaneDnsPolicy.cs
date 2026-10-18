using BoxForge.Models.Singbox;

namespace BoxForge.Configuration;

public static class ControlPlaneDnsPolicy
{
    public static DnsResolverOptions CreateResolver(AddressFamilyPolicy addressFamily) => new()
    {
        Server = SingboxTags.DirectAliDns,
        Strategy = addressFamily.ToDnsStrategy(),
        DisableOptimisticCache = true
    };
}
