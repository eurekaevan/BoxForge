namespace BoxForge.Models.Intermediate;

// Field is null only for omission; an explicit null still has a field.
// Evidence identifies a rule and field, without copying raw input or credentials.
internal sealed record SourceValue<T>(T Value, string? Field, string RuleId);

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
