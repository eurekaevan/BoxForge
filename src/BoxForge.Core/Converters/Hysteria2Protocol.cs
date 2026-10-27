using BoxForge.Models.Intermediate;
using BoxForge.Helpers;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;

namespace BoxForge.Converters;

internal static partial class Hysteria2Protocol
{
    [GeneratedRegex(@"\A\+?[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerBandwidth();

    [GeneratedRegex(@"\A([0-9]+)[ \t\r\n\f]*([KMGT]?)([Bb])ps\z", RegexOptions.CultureInvariant)]
    private static partial Regex BandwidthWithUnit();

    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: false, supportsUtls: false, forceTls: true))
        .Mapped("ports", "password")
        .Conditional("obfs", SourceFieldDisposition.Mapped,
            (_, value) => value is null or ""
                || value?.ToString() is "salamander" or "gecko"
                    ? null
                    : "仅支持 salamander 或 gecko")
        .Conditional("obfs-password", SourceFieldDisposition.Mapped,
            (node, _) => node.String("obfs") is null
                ? "指定了混淆密码，但未启用混淆"
                : null)
        .Mapped("up", "down",
            "hop-interval", "bbr-profile")
        .Alias("hop-interval", "hop_interval")
        .Alias("bbr-profile", "bbr_profile")
        .Unsupported("当前 Mihomo Hysteria2 不定义 network，不能生成目标网络限制", "network")
        .Mapped("obfs-min-packet-size", "obfs-max-packet-size")
        .Alias("obfs-min-packet-size", "obfs_min_packet_size")
        .Alias("obfs-max-packet-size", "obfs_max_packet_size")
        .Unsupported("QUIC 或 Realm 选项尚未映射",
            "realm-opts", "realm_opts", "udp-mtu", "udp_mtu", "handshake-timeout", "handshake_timeout",
            "initial-stream-receive-window", "initial-connection-receive-window",
            "max-stream-receive-window", "max-connection-receive-window");

    internal static Hysteria2Node Parse(
        NodeFieldReader node,
        string name)
    {
        string server = node.RequiredString("server");
        var ports = ParsePorts(node);

        Hysteria2Obfuscation? obfsConfig = null;
        string? obfsType = node.String("obfs");
        int? minPacketSize = ReadPacketSize(node, "obfs-min-packet-size");
        int? maxPacketSize = ReadPacketSize(node, "obfs-max-packet-size");
        string? packetSizeField = node.PresentField("obfs-min-packet-size")
            ?? node.PresentField("obfs-max-packet-size");
        if (obfsType != "gecko" && packetSizeField is not null)
            throw node.Error(packetSizeField, "字段 'obfs-min-packet-size/obfs-max-packet-size' 只适用于 gecko");
        if ((minPacketSize ?? 512) > (maxPacketSize ?? 1200))
            throw node.Error(packetSizeField!, "Gecko min_packet_size 不能大于 max_packet_size（缺失值为 512/1200）");
        if (obfsType != null)
        {
            obfsConfig = new Hysteria2Obfuscation(
                obfsType == "gecko" ? Hysteria2ObfuscationKind.Gecko : Hysteria2ObfuscationKind.Salamander,
                node.RawString("obfs-password") is { Length: > 0 } password
                    ? password : throw node.Error("obfs-password", "字段 'obfs-password' 不能为空"),
                new(minPacketSize, node.PresentField("obfs-min-packet-size") is not null),
                new(maxPacketSize, node.PresentField("obfs-max-packet-size") is not null));
        }

        var hopInterval = ParseHopInterval(node);
        Hysteria2BbrProfile? bbrProfile = node.OptionalEnum("bbr-profile",
            ("standard", Hysteria2BbrProfile.Standard),
            ("conservative", Hysteria2BbrProfile.Conservative),
            ("aggressive", Hysteria2BbrProfile.Aggressive));
        int? up = ParseBandwidth(node, "up");
        int? down = ParseBandwidth(node, "down");
        string nodePassword = node.RequiredRawString("password");
        var tls = TlsConfigHelper.ExtractSettings(node, server, forceTls: true, supportsUtls: false)
            ?? throw new InvalidOperationException("Hysteria2 interpretation unexpectedly returned no TLS settings.");
        return new Hysteria2Node(name, server,
            ports, nodePassword, up, down, hopInterval, bbrProfile, obfsConfig,
            new(node.OptionalBool("tls"), node.PresentField("tls") is not null), tls);
    }

    private static int? ReadPacketSize(NodeFieldReader node, string field)
    {
        int? size = node.OptionalInt(field, 0, 2048);
        return size == 0 ? null : size;
    }

    private static int? ParseBandwidth(NodeFieldReader node, string field)
    {
        string? value = node.RawString(field);
        if (value is null) return null;
        if (IntegerBandwidth().IsMatch(value)) value = value.TrimStart('+') + " Mbps";
        Match match = BandwidthWithUnit().Match(value);
        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong quantity))
            throw node.Error(field, $"字段 '{field}' 带宽格式无效；必须符合 Mihomo 的整数单位语法");
        ulong multiplier = match.Groups[2].Value switch
        {
            "K" => 1_000,
            "M" => 1_000_000,
            "G" => 1_000_000_000,
            "T" => 1_000_000_000_000,
            _ => 1
        };
        if (quantity > ulong.MaxValue / multiplier)
            throw node.Error(field, $"字段 '{field}' 带宽超出源解析器范围");
        ulong amount = quantity * multiplier;
        // Mihomo stores bytes/s; sing-box accepts integral megabits/s only.
        ulong divisor = match.Groups[3].Value == "B" ? 125_000UL : 1_000_000UL;
        if (amount % divisor != 0 || amount / divisor > int.MaxValue)
            throw node.Error(field, $"字段 '{field}' 无法精确映射为整数 Mbps（不得舍入或截断）");
        return (int)(amount / divisor);
    }

    private static Hysteria2HopInterval? ParseHopInterval(NodeFieldReader node)
    {
        string field = node.PresentField("hop-interval") ?? "hop-interval";
        string? value = node.RawString(field);
        if (value is null) return null;
        string[] parts = value.Trim().Split('-').Select(part => part.Trim('[', ' ', ']')).ToArray();
        if (value.Trim().Length == 0) return new(30, null);
        if (parts.Length > 2 || parts.Any(part =>
                !ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw node.Error(field, "字段 'hop-interval' 只支持 Mihomo 的整数秒数或 min-max 范围");
        ulong start = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
        ulong end = parts.Length == 2 ? ulong.Parse(parts[1], CultureInfo.InvariantCulture) : start;
        if (start > end) (start, end) = (end, start);
        start = start == 0 ? 30 : Math.Max(start, 5);
        end = Math.Max(end, start);
        if (end > long.MaxValue / 1_000_000_000)
            throw node.Error(field, "字段 'hop-interval' 超出 Go duration 范围");
        return new((long)start, parts.Length == 2 ? (long)end : null);
    }

    private static ReadOnlyCollection<Hysteria2PortSpan> ParsePorts(
        NodeFieldReader node)
    {
        string? portsValue = node.String("ports");
        if (portsValue == null)
        {
            return Array.AsReadOnly(new[] { new Hysteria2PortSpan(node.RequiredPort("port"), null) });
        }

        string[] entries = portsValue.Split(
            ',',
            StringSplitOptions.TrimEntries);
        if (entries.Any(string.IsNullOrWhiteSpace))
        {
            throw node.Error("ports", "ports 包含空端口项");
        }

        return Array.AsReadOnly(entries.Select(entry => NormalizePortEntry(node, entry)).ToArray());
    }

    private static Hysteria2PortSpan NormalizePortEntry(NodeFieldReader node, string entry)
    {
        string[] range = entry.Split(
            '-',
            StringSplitOptions.TrimEntries);
        if (range.Length == 1)
        {
            return new(ParsePort(node, range[0]), null);
        }

        if (range.Length != 2
            || string.IsNullOrWhiteSpace(range[0])
            || string.IsNullOrWhiteSpace(range[1]))
        {
            throw node.Error("ports", $"ports 中的端口范围格式无效: {entry}");
        }

        int start = ParsePort(node, range[0]);
        int end = ParsePort(node, range[1]);
        if (start > end)
        {
            throw node.Error("ports", $"ports 中的端口范围起点不能大于终点: {entry}");
        }

        return new(start, end);
    }

    private static int ParsePort(NodeFieldReader node, string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535)
        {
            return port;
        }

        throw node.Error("ports",
            $"ports 包含无效端口 '{value}'，端口必须为 1-65535 的整数");
    }
}
