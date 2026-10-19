namespace BoxForge.Converters;

internal static class TlsSemantics
{
    private const string Source =
        "https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/trojan.go";
    private const string Target =
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/common/tls/std_client.go";

    internal static readonly SemanticMapping TrojanActivation = new(
        "trojan.tls", "tls",
        "Native TCP Trojan always uses TLS; tls is a BoxForge compatibility field, not a Mihomo Trojan switch.",
        "Emit enabled TLS on omission or explicit true; BoxForge rejects explicit false or null.",
        Source,
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/protocol/trojan/outbound.go",
        SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping ServerName = new(
        "tls.server-name", "sni",
        "Use the supplied TLS name, falling back to server; BoxForge also accepts servername and preserves its existing trimming.",
        "Emit the interpreted name as server_name, including the server fallback.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping CertificateValidation = new(
        "tls.certificate-validation", "skip-cert-verify",
        "Explicit true disables certificate validation; false enables it. Preserve omission separately.",
        "Emit the explicit boolean as insecure; leave omitted/null values unset.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping ApplicationProtocols = new(
        "tls.application-protocols", "alpn",
        "Keep explicit protocol order and duplicates, and distinguish omission from an empty list.",
        "Preserve the existing alpn values and omission/empty-list serialization; this migration does not change defaults.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping ClientFingerprint = new(
        "tls.client-fingerprint", "client-fingerprint",
        "BoxForge accepts its existing shared modern profiles; empty/none selects native TLS. Other profiles remain rejected.",
        "Emit supported uTLS profiles; omit utls for native TLS.",
        "https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/component/tls/utls.go",
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/common/tls/utls_client.go",
        SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping RealityPublicKey = new(
        "tls.reality.public-key", "public-key",
        "BoxForge accepts a 32-byte Base64URL Reality public key; keep the accepted key unchanged.",
        "Emit the accepted key as reality.public_key.",
        Source,
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/common/tls/reality_client.go",
        SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping RealityShortId = new(
        "tls.reality.short-id", "short-id",
        "BoxForge accepts zero to eight bytes of even-length hexadecimal, preserving case after existing trimming.",
        "Emit the accepted value as reality.short_id, including an explicitly empty string.",
        Source,
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/common/tls/reality_client.go",
        SourceFieldDisposition.Mapped);
}
