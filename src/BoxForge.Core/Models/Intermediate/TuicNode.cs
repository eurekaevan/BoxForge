namespace BoxForge.Models.Intermediate;

internal sealed record TuicNode(
    string Name,
    string Server,
    int Port,
    string UserId,
    string Password,
    string? CongestionControl,
    string? UdpRelayMode,
    bool? UdpOverStream,
    bool? ZeroRttHandshake,
    string KeepAlivePeriod,
    SourceValue<bool?> TlsEnabled,
    TlsSettings Tls) : ProxyNode(Name, Server)
{
    internal override void Validate()
    {
        if (TlsEnabled.Value == false || (TlsEnabled.IsSpecified && TlsEnabled.Value != true))
            throw new InvalidOperationException("TUIC requires TLS; an explicit TLS switch must be true.");
        if (Tls.Reality is not null || Tls.ClientFingerprint is not null)
            throw new InvalidOperationException("TUIC QUIC TLS does not support Reality or uTLS.");
        if (UdpOverStream == true && UdpRelayMode is not null)
            throw new InvalidOperationException("TUIC UDP over stream conflicts with UDP relay mode.");
        Tls.Validate();
    }
}
