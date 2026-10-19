namespace BoxForge.Converters;

internal static class AnyTlsSemantics
{
    private const string Source =
        "https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/anytls.go";
    private const string Target =
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/protocol/anytls/outbound.go";

    internal static readonly SemanticMapping Tls = new(
        "anytls.tls", "tls",
        "Native AnyTLS always uses TLS; tls is a BoxForge compatibility field, not a Mihomo AnyTLS switch.",
        "Emit enabled TLS on omission or true; reject explicit false/null and unsupported Reality.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping IdleTimeout = new(
        "anytls.idle-timeout", "idle-session-timeout",
        "Mihomo declares integer seconds; BoxForge also accepts positive Go duration strings. Preserve existing rejection of zero/negative values.",
        "Emit explicit seconds with s, retain accepted duration text; omit absent/null values without inventing defaults.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping IdleCheckInterval = new(
        "anytls.idle-check-interval", "idle-session-check-interval",
        "Mihomo declares integer seconds; BoxForge also accepts positive Go duration strings. Preserve existing rejection of zero/negative values.",
        "Emit idle_session_check_interval using the same positive-duration interpretation as idle timeout.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping MinimumIdleSessions = new(
        "anytls.minimum-idle-sessions", "min-idle-session",
        "Keep the accepted nonnegative int32 value, including zero; absence/null leaves runtime default handling unchanged.",
        "Emit min_idle_session when present and non-null, preserving zero.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping ClientMetadata = new(
        "anytls.client-metadata", "client-metadata",
        "Keep explicit metadata including empty strings and whitespace; BoxForge rejects LF and UTF-8 values over 65479 bytes.",
        "Emit client_metadata verbatim; omit absence/null and never invent software, version, platform or identity metadata.",
        Source, Target, SourceFieldDisposition.Mapped);
}
