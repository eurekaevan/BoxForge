using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public class ExperimentalBuilder
{
    public ExperimentalConfig Build(string cacheId)
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
