namespace BoxForge.Converters;

internal static class Hysteria2Semantics
{
    private const string Source =
        "https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/hysteria2.go";
    private const string Target =
        "https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/protocol/hysteria2/outbound.go";

    internal static readonly SemanticMapping Tls = new(
        "hysteria2.tls", "tls", "QUIC requires TLS; tls is a BoxForge compatibility switch.",
        "Require TLS, reject explicit false/null, Reality and uTLS.", Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping Ports = new(
        "hysteria2.ports", "ports", "Nonempty ports takes precedence over port; retain source order, scalar ports and explicit ranges.",
        "Emit server_port for one scalar; otherwise emit server_ports as start:end ranges, including equal-endpoint ranges for list scalars.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping Upload = new(
        "hysteria2.upload", "up", "Interpret Mihomo integer bandwidth units; BoxForge additionally treats plain integers as Mbps.",
        "Keep only exact int32 Mbps values; reject rounding, truncation and source arithmetic overflow. Preserve zero and omission separately.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping Download = new(
        "hysteria2.download", "down", "Use the same exact bandwidth interpretation as up.",
        "Emit down_mbps without injecting a bandwidth default.", Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping HopInterval = new(
        "hysteria2.hop-interval", "hop-interval", "Integer seconds or min-max; reorder bounds, use 30s for zero, and clamp positive minimum to 5s.",
        "Preserve fixed versus ranged intervals and int64 nanosecond bounds; leave omission to existing tuning.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping BbrProfile = new(
        "hysteria2.bbr-profile", "bbr-profile", "Accept standard, conservative and aggressive profiles; retain omission/null.",
        "Emit an explicit profile unchanged; existing tuning applies only when no profile and no positive bandwidth are supplied.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping Obfuscation = new(
        "hysteria2.obfuscation", "obfs", "Accept Salamander or Gecko and preserve the nonempty raw obfuscation password.",
        "Emit obfs only when enabled; empty/null disables it under the existing source schema.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping MinimumPacketSize = new(
        "hysteria2.gecko-minimum-packet-size", "obfs-min-packet-size", "Gecko only; zero uses the core default, otherwise accept 1..2048.",
        "Keep explicit nonzero size and source origin; validate against maximum using existing 512/1200 defaults.",
        Source, Target, SourceFieldDisposition.Mapped);

    internal static readonly SemanticMapping MaximumPacketSize = new(
        "hysteria2.gecko-maximum-packet-size", "obfs-max-packet-size", "Gecko only; use the same bounds and zero-default interpretation as minimum.",
        "Emit an explicit nonzero max_packet_size; retain the actual source field when null or zero selects defaults.",
        Source, Target, SourceFieldDisposition.Mapped);
}
