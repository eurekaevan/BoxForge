namespace BoxForge.Models.Intermediate;

internal abstract record ProxyNode(string Name, string Server)
{
    internal ConnectionSettings Connection { get; init; } = new();

    internal abstract void Validate();
}

internal sealed record ConnectionSettings(
    string? ConnectTimeout = null,
    string? TcpKeepAlive = null,
    string? TcpKeepAliveInterval = null);
