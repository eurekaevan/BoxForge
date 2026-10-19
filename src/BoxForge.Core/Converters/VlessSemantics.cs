namespace BoxForge.Converters;

// Implementation status is separate from verification: check does not prove
// runtime equivalence. URLs pin both sides to the researched revisions.
internal sealed record SemanticMapping(
    string Id,
    string SourceField,
    string SourceMeaning,
    string TargetMeaning,
    string SourceEvidence,
    string TargetEvidence,
    SourceFieldDisposition Status);

internal static class VlessSemantics
{
    private const string Source =
        "https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/vless.go";
    private const string Target =
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/protocol/vless/outbound.go";

    internal static readonly SemanticMapping Flow = new(
        "vless.flow", "flow",
        "UTF-8 shorter than 16 bytes disables flow; otherwise accept only the xtls-rprx-vision prefix.",
        "Omit flow when disabled; emit xtls-rprx-vision for Vision.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping DatagramEncoding = new(
        "vless.datagram-encoding", "packet-encoding",
        "With packet-addr absent, packet/packetaddr select packet addressing; all other values and omission select XUDP.",
        "Emit packetaddr or xudp explicitly; do not depend on target defaults.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping Tls = new(
        "vless.tls", "tls",
        "The tls switch defaults to false; TLS and Reality options also influence connection setup.",
        "BoxForge requires explicit true and a supported fingerprint for Reality; otherwise reject. Emit TLS only when enabled.",
        Source, Target, SourceFieldDisposition.Mapped);
}
