using System.Text.Json.Serialization;

namespace BoxForge.Configuration;

public sealed record PlatformProfile
{
    [JsonConstructor]
    private PlatformProfile() { }

    public required bool IsMobile { get; init; }
    public required AddressFamilyPolicy AddressFamily { get; init; }
    public required bool? StrictRoute { get; init; }
    public required bool? AutoRedirect { get; init; }
    public required bool? MultiQueue { get; init; }
    public required bool UseDirectForwarding { get; init; }
    public required string? TcpKeepAlive { get; init; }
    public required string? TcpKeepAliveInterval { get; init; }
    public required string UrlTestInterval { get; init; }
    public required DnsResolverSelectionMode DnsSelectionMode { get; init; }
    public required string TaildropDirectory { get; init; }
}
