namespace BoxForge.Models.Intermediate;

internal sealed record ShadowsocksNode(
    string Name,
    string Server,
    int Port,
    SourceValue<string> Cipher,
    string Password,
    SourceValue<bool?> UdpOverTcpEnabled,
    SourceValue<ShadowsocksUotVersion> UdpOverTcpVersion,
    SourceValue<ShadowsocksObfs?> Plugin) : ProxyNode(Name, Server)
{
    internal override void Validate()
    {
        if (Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Cipher.Value) || string.IsNullOrEmpty(Password))
            throw new InvalidOperationException("Shadowsocks requires a valid port, cipher and password.");
        if (UdpOverTcpVersion.Value is not (ShadowsocksUotVersion.V1 or ShadowsocksUotVersion.V2))
            throw new InvalidOperationException("Shadowsocks UoT requires an interpreted v1 or v2 version.");
        if (Plugin.Value is { } plugin && !ShadowsocksObfs.IsValidHost(plugin.Host.Value))
            throw new InvalidOperationException("Shadowsocks obfs requires a nonempty host without control characters.");
    }
}

internal enum ShadowsocksUotVersion { V1 = 1, V2 = 2 }

internal enum ShadowsocksObfsMode { Http, Tls }

internal sealed record ShadowsocksObfs(
    SourceValue<ShadowsocksObfsMode> Mode,
    SourceValue<string> Host)
{
    internal static bool IsValidHost(string host) =>
        !string.IsNullOrWhiteSpace(host) && !host.Any(char.IsControl);
}
