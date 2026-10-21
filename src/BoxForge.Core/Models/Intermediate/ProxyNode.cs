using BoxForge.Configuration;

namespace BoxForge.Models.Intermediate;

internal abstract record ProxyNode(string Name, string Server)
{
    internal ConnectionSettings Connection { get; init; } = new();
    internal AddressFamilyPolicy? AddressFamily { get; init; }

    internal abstract void Validate();
}

internal sealed record ConnectionSettings(
    string? ConnectTimeout = null,
    string? TcpKeepAlive = null,
    string? TcpKeepAliveInterval = null);
