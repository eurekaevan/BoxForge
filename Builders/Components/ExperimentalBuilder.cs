using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public static class ExperimentalBuilder
{
    public static ExperimentalConfig Build(string? cacheId)
    {
        return new ExperimentalConfig
        {
            CacheFile = new CacheFileConfig
            {
                Enabled = true,
                Path = "cache.db",
                StoreDns = true,
                CacheId = cacheId
            }
        };
    }
}
