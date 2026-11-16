using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BoxForge.Helpers;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class TuicProtocol
{
    private static readonly IReadOnlyList<string> DefaultAlpn = Array.AsReadOnly(new[] { "h3" });

    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: false, supportsUtls: false, forceTls: true))
        .Mapped("uuid", "password", "ip", "heartbeat-interval", "disable-sni", "reduce-rtt",
            "udp-over-stream", "udp-over-stream-version")
        .Conditional("congestion-controller", SourceFieldDisposition.Mapped,
            (_, value) => value is null or "" or "cubic" or "new_reno" or "bbr"
                ? null : "仅支持 cubic、new_reno 或 bbr")
        .Conditional("udp-relay-mode", SourceFieldDisposition.Mapped,
            (_, value) => value is null or "" or "native" or "quic"
                ? null : "仅支持 native 或 quic")
        .Conditional("request-timeout", SourceFieldDisposition.IgnoredByDesign,
            (node, _) =>
            {
                node.OptionalInt("request-timeout");
                return null;
            })
        .Conditional("fast-open", SourceFieldDisposition.IgnoredByDesign,
            (node, _) => node.Bool("fast-open") ? "TUIC v4 Fast Open 无法映射到 v5" : null)
        .Unsupported("目标仅支持 TUIC v5（uuid/password），不支持 v4 token", "token")
        .Unsupported("TUIC 性能或网络选项尚未等价映射",
            "max-open-streams", "max-udp-relay-packet-size", "cwnd", "bbr-profile",
            "recv-window-conn", "recv-window", "disable-mtu-discovery", "max-datagram-frame-size", "network");

    internal static TuicNode Parse(NodeFieldReader node, string name)
    {
        string server = node.RequiredString("server");
        int port = node.RequiredPort("port");
        string uuid = node.RequiredString("uuid");
        if (!Guid.TryParseExact(uuid, "D", out _))
            throw node.Error("uuid", "字段 'uuid' 必须是标准 UUID（xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx）");
        string password = node.RawString("password")
            ?? throw node.Error("password", "缺失必填字段或为 null: password（无密码请显式填写空字符串）");
        var tls = TlsConfigHelper.ExtractSettings(node, server, forceTls: true, supportsUtls: false)
            ?? throw new InvalidOperationException("TUIC interpretation unexpectedly returned no TLS settings.");
        if (tls.ApplicationProtocols is { Count: 0 })
            throw node.Error("alpn", "TUIC 的 ALPN 不能为空列表");
        bool? disableSni = node.OptionalBool("disable-sni");
        tls = tls with
        {
            ApplicationProtocols = tls.ApplicationProtocols ?? DefaultAlpn,
            DisableSni = disableSni,
            // Mihomo clears ServerName and disables verification when disable-sni is true.
            AllowInvalidCertificate = disableSni == true ? true : tls.AllowInvalidCertificate
        };
        if (node.RawString("ip") is { Length: > 0 } ip)
        {
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw node.Error("ip", "字段 'ip' 必须是 IPv4 地址；保持原 server/sni 用于 TLS 名称");
            server = address.ToString();
        }
        int heartbeat = node.OptionalInt("heartbeat-interval") ?? 10000;
        if (heartbeat <= 0) heartbeat = 10000;
        bool? udpOverStream = node.OptionalBool("udp-over-stream");
        int version = node.OptionalInt("udp-over-stream-version", 0, 2) ?? 0;
        if (udpOverStream == true && version != 2)
            throw node.Error("udp-over-stream-version", "启用 udp-over-stream 必须显式指定版本 2；源默认版本 1 无法等价映射");
        string? relay = node.String("udp-relay-mode");
        if (udpOverStream == true && relay is not null)
            throw node.Error("udp-relay-mode", "不能与 udp-over-stream 同时指定");
        return new TuicNode(name, server, port, uuid, password,
            node.String("congestion-controller"), relay, udpOverStream,
            node.OptionalBool("reduce-rtt"), heartbeat.ToString(CultureInfo.InvariantCulture) + "ms",
            new(node.OptionalBool("tls"), node.PresentField("tls") is not null), tls);
    }
}
