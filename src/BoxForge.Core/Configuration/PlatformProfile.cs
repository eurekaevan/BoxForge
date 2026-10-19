using BoxForge.Models;

namespace BoxForge.Configuration;

public sealed record PlatformProfile
{
    private PlatformProfile() { }

    public bool IsMobile { get; init; }
    public AddressFamilyPolicy AddressFamily { get; init; } = AddressFamilyPolicy.Ipv4Only;
    public bool? StrictRoute { get; init; }
    public bool? AutoRedirect { get; init; }
    public bool? MultiQueue { get; init; }
    public bool UseDirectForwarding { get; init; }
    public string? TcpKeepAlive { get; init; }
    public string? TcpKeepAliveInterval { get; init; }
    public string UrlTestInterval { get; init; } = "3m";
    public DnsResolverSelectionMode DnsSelectionMode { get; init; }
    public string TaildropDirectory { get; init; } = "Taildrop";

    private static readonly PlatformProfile Mobile = new()
    {
        IsMobile = true,
        DnsSelectionMode = DnsResolverSelectionMode.SequentialFallback
    };

    private static readonly PlatformProfile Desktop = new()
    {
        StrictRoute = true,
        TcpKeepAlive = "1m",
        TcpKeepAliveInterval = "30s",
        UrlTestInterval = "1m",
        DnsSelectionMode = DnsResolverSelectionMode.ParallelFastest
    };

    private static readonly PlatformProfile Linux = Desktop with
    {
        AutoRedirect = true,
        MultiQueue = true,
        UseDirectForwarding = true,
        TaildropDirectory = "$HOME/Downloads/Taildrop"
    };

    private static readonly PlatformProfile Windows = Desktop with
    {
        TaildropDirectory = "$USERPROFILE\\Downloads\\Taildrop"
    };

    public static PlatformProfile For(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Android => Mobile,
        TargetPlatform.Linux => Linux,
        TargetPlatform.Windows => Windows,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
    };
}
