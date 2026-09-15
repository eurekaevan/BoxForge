using BoxForge.Models;

namespace BoxForge.Configuration;

public sealed class TailscaleOptions
{
    public bool Enabled { get; set; } = true;

    public bool AndroidEnabled { get; set; } = true;

    public bool IsEnabled(TargetPlatform platform) =>
        platform == TargetPlatform.Android
            ? AndroidEnabled
            : Enabled;
}
