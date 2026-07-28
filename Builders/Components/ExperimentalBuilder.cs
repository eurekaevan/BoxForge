using SubConvert.Models;
using SubConvert.Models.Singbox;

namespace SubConvert.Builders.Components;

public class ExperimentalBuilder
{
    public ExperimentalConfig Build(TargetPlatform platform, string cacheId)
    {
        return new ExperimentalConfig
        {
            CacheFile = new CacheFileConfig
            {
                Enabled = true,
                Path = "cache.db",
                CacheId = cacheId
            },
            ClashApi = platform != TargetPlatform.Android
                ? new ClashApiConfig
                {
                    ExternalController = "127.0.0.1:9090",
                    ExternalUi = platform == TargetPlatform.Windows
                        ? "ui"
                        : "/etc/sing-box/ui",
                    Secret = "127001"
                }
                : null
        };
    }
}
