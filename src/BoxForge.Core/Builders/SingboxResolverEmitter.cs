using BoxForge.Configuration;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxResolverEmitter
{
    internal static DnsResolverOptions Emit(DomainResolverPlan resolver) => new()
    {
        Server = resolver.Server,
        Strategy = resolver.AddressFamily.ToDnsStrategy(),
        DisableOptimisticCache = resolver.DisableOptimisticCache
    };
}
