using BoxForge.Models;

namespace BoxForge.Configuration;

public sealed class TailscaleOptions
{
    public bool Enabled { get; set; }

    public bool AndroidEnabled { get; set; }

    public bool IsEnabled(TargetPlatform platform) =>
        platform == TargetPlatform.Android
            ? AndroidEnabled
            : Enabled;
}
