namespace BoxForge.Models.Intermediate;

// Preserve explicit null separately from omission where runtime validation needs it.
internal readonly record struct SourceValue<T>(T Value, bool IsSpecified);

internal sealed record TlsSettings(
    SourceValue<string> ServerName,
    SourceValue<bool?> AllowInvalidCertificate,
    SourceValue<IReadOnlyList<string>?> ApplicationProtocols,
    SourceValue<string?> ClientFingerprint,
    RealitySettings? Reality)
{
    internal void Validate()
    {
        if (Reality is not null && string.IsNullOrEmpty(ClientFingerprint.Value))
            throw new InvalidOperationException("Reality requires a client TLS fingerprint.");
    }
}

internal sealed record RealitySettings(SourceValue<string> PublicKey, SourceValue<string> ShortId);
