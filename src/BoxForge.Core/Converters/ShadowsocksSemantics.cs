namespace BoxForge.Converters;

internal static class ShadowsocksSemantics
{
    private const string Source =
        "https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/shadowsocks.go";
    private const string Target =
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/protocol/shadowsocks/outbound.go";

    internal static readonly SemanticMapping Cipher = new(
        "shadowsocks.cipher", "cipher", "Keep the existing normalized cipher; BoxForge also accepts method as an exclusive alias.",
        "Emit method without adding a cipher allowlist; official check remains the target capability gate.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping UotEnabled = new(
        "shadowsocks.uot-enabled", "udp-over-tcp", "UoT is opt-in; retain omission, explicit null, false and true.",
        "Emit udp_over_tcp only when enabled; do not inject a disabled object.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping UotVersion = new(
        "shadowsocks.uot-version", "udp-over-tcp-version", "Omission, null and zero select legacy v1; explicit 1/2 select the corresponding wire version.",
        "Emit version 1 or 2 explicitly when UoT is enabled; reject other source versions even when disabled.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping Plugin = new(
        "shadowsocks.plugin", "plugin", "BoxForge supports only obfs; omission, null and empty string disable it.",
        "Emit obfs-local with structured, validated options lowered to SIP003 text; v2ray-plugin remains rejected.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping ObfsMode = new(
        "shadowsocks.obfs-mode", "mode", "BoxForge requires an explicit http or tls obfs mode.",
        "Emit obfs=http or obfs=tls; do not store SIP003 text in the node IR.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping ObfsHost = new(
        "shadowsocks.obfs-host", "host", "Omission/null uses bing.com; keep explicit raw text and reject blank/control-character hosts.",
        "SIP003-escape backslash, semicolon and equals only during emission, preserving source text in IR.",
        Source, Target, SourceFieldDisposition.Mapped);
}
