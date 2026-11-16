namespace BoxForge.Models.Intermediate;

// Preserve explicit null separately from omission where runtime validation needs it.
internal readonly record struct SourceValue<T>(T Value, bool IsSpecified);

internal sealed record TlsSettings(
    string ServerName,
    bool? AllowInvalidCertificate,
    IReadOnlyList<string>? ApplicationProtocols,
    string? ClientFingerprint,
    RealitySettings? Reality,
    string? Certificate,
    string? CertificateSha256)
{
    internal bool? DisableSni { get; init; }

    internal void Validate()
    {
        if (Reality is not null && string.IsNullOrEmpty(ClientFingerprint))
            throw new InvalidOperationException("Reality requires a client TLS fingerprint.");
    }
}

internal sealed record RealitySettings(string PublicKey, string ShortId);
