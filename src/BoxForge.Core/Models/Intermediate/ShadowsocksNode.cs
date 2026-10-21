namespace BoxForge.Models.Intermediate;

internal sealed record ShadowsocksNode(
    string Name,
    string Server,
    int Port,
    string Cipher,
    string Password,
    bool? UdpOverTcpEnabled,
    ShadowsocksUotVersion UdpOverTcpVersion,
    ShadowsocksObfs? Plugin) : ProxyNode(Name, Server)
{
    internal override void Validate()
    {
        if (Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Cipher) || string.IsNullOrEmpty(Password))
            throw new InvalidOperationException("Shadowsocks requires a valid port, cipher and password.");
        if (UdpOverTcpVersion is not (ShadowsocksUotVersion.V1 or ShadowsocksUotVersion.V2))
            throw new InvalidOperationException("Shadowsocks UoT requires an interpreted v1 or v2 version.");
        if (Plugin is { } plugin && !ShadowsocksObfs.IsValidHost(plugin.Host))
            throw new InvalidOperationException("Shadowsocks obfs requires a nonempty host without control characters.");
    }
}

internal enum ShadowsocksUotVersion { V1 = 1, V2 = 2 }

internal enum ShadowsocksObfsMode { Http, Tls }

internal sealed record ShadowsocksObfs(
    ShadowsocksObfsMode Mode,
    string Host)
{
    internal static bool IsValidHost(string host) =>
        !string.IsNullOrWhiteSpace(host) && !host.Any(char.IsControl);
}
