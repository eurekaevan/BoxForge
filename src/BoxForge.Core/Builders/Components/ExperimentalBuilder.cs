using BoxForge.Configuration;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

internal static class ExperimentalBuilder
{
    public static ExperimentalConfig Build(string? cacheId, DnsAddressMode addressMode = DnsAddressMode.RealIp)
    {
        return new ExperimentalConfig
        {
            CacheFile = new CacheFileConfig
            {
                Enabled = true,
                Path = "cache.db",
                StoreDns = true,
                StoreFakeIp = addressMode == DnsAddressMode.FakeIp ? true : null,
                CacheId = cacheId
            }
        };
    }
}
