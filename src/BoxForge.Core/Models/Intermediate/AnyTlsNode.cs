using System.Text;
using BoxForge.Helpers;

namespace BoxForge.Models.Intermediate;

internal sealed record AnyTlsNode(
    string Name,
    string Server,
    int Port,
    string Password,
    SourceValue<string?> IdleTimeout,
    SourceValue<string?> IdleCheckInterval,
    SourceValue<int?> MinimumIdleSessions,
    SourceValue<string?> ClientMetadata,
    SourceValue<bool?> TlsEnabled,
    TlsSettings Tls) : ProxyNode(Name, Server)
{
    internal override void Validate()
    {
        if (Tls is null || TlsEnabled.Value == false
            || (TlsEnabled.IsSpecified && TlsEnabled.Value != true))
            throw new InvalidOperationException("AnyTLS requires TLS; an explicit TLS switch must be true.");
        if (Tls.Reality is not null)
            throw new InvalidOperationException("AnyTLS does not support Reality.");
        if (MinimumIdleSessions.Value < 0)
            throw new InvalidOperationException("AnyTLS minimum idle sessions cannot be negative.");
        if (ClientMetadata.Value is { } metadata && !IsValidMetadata(metadata))
            throw new InvalidOperationException("AnyTLS client metadata violates the settings frame limits.");
        if ((IdleTimeout.Value is { } timeout && !GoDurationHelper.IsPositiveDuration(timeout))
            || (IdleCheckInterval.Value is { } interval && !GoDurationHelper.IsPositiveDuration(interval)))
            throw new InvalidOperationException("AnyTLS session durations must be positive Go durations.");
        Tls?.Validate();
    }

    // The unescaped newline-delimited settings frame has a uint16 length.
    internal static bool IsValidMetadata(string metadata) =>
        !metadata.Contains('\n') && Encoding.UTF8.GetByteCount(metadata) <= ushort.MaxValue - 56;
}
