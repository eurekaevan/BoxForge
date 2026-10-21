namespace BoxForge.Models.Intermediate;

// Interpreted connection semantics, without JSON or target types.
internal sealed record VlessNode(
    string Name,
    string Server,
    int Port,
    string UserId,
    VlessFlow Flow,
    DatagramEncoding DatagramEncoding,
    bool? TlsEnabled,
    TlsSettings? Tls) : ProxyNode(Name, Server)
{
    internal override void Validate()
    {
        if ((TlsEnabled == true) != (Tls is not null))
            throw new InvalidOperationException("VLESS TLS activation must match the interpreted source setting.");
        Tls?.Validate();
    }
}

internal enum VlessFlow { None, Vision }

internal enum DatagramEncoding { Xudp, PacketAddress }

