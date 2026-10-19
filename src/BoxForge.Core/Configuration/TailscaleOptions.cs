namespace BoxForge.Configuration;

public sealed class TailscaleOptions
{
    public bool Enabled { get; set; } = true;

    public bool AndroidEnabled { get; set; } = true;

    public bool IsEnabled(PlatformProfile platform) =>
        platform.IsMobile
            ? AndroidEnabled
            : Enabled;
}
