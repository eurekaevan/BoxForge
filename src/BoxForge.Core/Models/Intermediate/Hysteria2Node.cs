namespace BoxForge.Models.Intermediate;

internal sealed record Hysteria2Node(
    string Name,
    string Server,
    IReadOnlyList<Hysteria2PortSpan> Ports,
    string Password,
    int? UploadMegabitsPerSecond,
    int? DownloadMegabitsPerSecond,
    Hysteria2HopInterval? HopInterval,
    Hysteria2BbrProfile? BbrProfile,
    Hysteria2Obfuscation? Obfuscation,
    SourceValue<bool?> TlsEnabled,
    TlsSettings Tls) : ProxyNode(Name, Server)
{
    internal Hysteria2HopInterval? TuningHopInterval { get; init; }
    internal Hysteria2BbrProfile? TuningBbrProfile { get; init; }

    internal override void Validate()
    {
        if (Tls is null || TlsEnabled.Value == false
            || (TlsEnabled.IsSpecified && TlsEnabled.Value != true))
            throw new InvalidOperationException("Hysteria2 requires TLS; an explicit TLS switch must be true.");
        if (Tls.Reality is not null || Tls.ClientFingerprint is not null)
            throw new InvalidOperationException("Hysteria2 QUIC TLS does not support Reality or uTLS.");
        if (Ports.Count == 0 || Ports.Any(port =>
                port.Start is < 1 or > 65535 || port.End is < 1 or > 65535 || port.End < port.Start))
            throw new InvalidOperationException("Hysteria2 requires valid, ordered port spans.");
        if (UploadMegabitsPerSecond < 0 || DownloadMegabitsPerSecond < 0)
            throw new InvalidOperationException("Hysteria2 bandwidth cannot be negative.");
        if ((HopInterval ?? TuningHopInterval) is { } hop
            && (hop.MinimumSeconds < 5 || hop.MinimumSeconds > long.MaxValue / 1_000_000_000
                || hop.MaximumSeconds < hop.MinimumSeconds || hop.MaximumSeconds > long.MaxValue / 1_000_000_000))
            throw new InvalidOperationException("Hysteria2 hop interval must be an ordered positive range within int64 nanoseconds.");
        if (Obfuscation is { } obfs)
        {
            if (string.IsNullOrEmpty(obfs.Password))
                throw new InvalidOperationException("Hysteria2 obfuscation requires a password.");
            if (obfs.MinimumPacketSize.Value is < 1 or > 2048 || obfs.MaximumPacketSize.Value is < 1 or > 2048
                || (obfs.MinimumPacketSize.Value ?? 512) > (obfs.MaximumPacketSize.Value ?? 1200))
                throw new InvalidOperationException("Hysteria2 Gecko packet sizes must be valid and ordered.");
            if (obfs.Kind != Hysteria2ObfuscationKind.Gecko
                && (obfs.MinimumPacketSize.IsSpecified || obfs.MaximumPacketSize.IsSpecified
                    || obfs.MinimumPacketSize.Value is not null || obfs.MaximumPacketSize.Value is not null))
                throw new InvalidOperationException("Hysteria2 packet sizes apply only to Gecko.");
        }
        Tls?.Validate();
    }
}

// A null End is a scalar port. Preserve an explicit equal-endpoint range.
internal sealed record Hysteria2PortSpan(int Start, int? End);

// A null maximum is fixed, including the explicit empty-source 30s default.
internal sealed record Hysteria2HopInterval(long MinimumSeconds, long? MaximumSeconds);

internal enum Hysteria2BbrProfile { Standard, Conservative, Aggressive }

internal enum Hysteria2ObfuscationKind { Salamander, Gecko }

internal sealed record Hysteria2Obfuscation(
    Hysteria2ObfuscationKind Kind,
    string Password,
    SourceValue<int?> MinimumPacketSize,
    SourceValue<int?> MaximumPacketSize);
