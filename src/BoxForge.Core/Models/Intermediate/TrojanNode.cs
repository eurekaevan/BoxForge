namespace BoxForge.Models.Intermediate;

internal sealed record TrojanNode(
    string Name,
    string Server,
    int Port,
    string Password,
    SourceValue<bool?> TlsEnabled,
    TlsSettings Tls) : ProxyNode(Name, Server)
{
    internal override void Validate()
    {
        if (Tls is null || TlsEnabled.Value == false
            || (TlsEnabled.Field is not null && TlsEnabled.Value != true))
            throw new InvalidOperationException("Trojan requires TLS; an explicit TLS switch must be true.");
        Tls?.Validate();
    }
}
