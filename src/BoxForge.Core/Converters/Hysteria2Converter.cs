using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BoxForge.Converters;

public sealed class Hysteria2Converter()
    : ProxyConverterBase("Hysteria2", "hysteria2")
{
    private static readonly SourceFieldSchema SourceFields = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: false, supportsUtls: false, forceTls: true))
        .Mapped("ports", "password")
        .Conditional("obfs", SourceFieldDisposition.Mapped,
            (_, value) => value is null or ""
                || value?.ToString() is "salamander" or "gecko"
                    ? null
                    : "仅支持 salamander 或 gecko")
        .Conditional("obfs-password", SourceFieldDisposition.Mapped,
            (node, _) => node.GetString("obfs") is null
                ? "指定了混淆密码，但未启用混淆"
                : null)
        .Mapped("up", "down", "hop-interval", "bbr-profile")
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

    protected override SourceFieldSchema Schema => SourceFields;

    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");
        var (serverPort, serverPorts) = ParsePorts(node);

        OutboundObfs? obfsConfig = null;
        string? obfsType = node.GetString("obfs");
        int? minPacketSize = ReadPacketSize(node, "obfs-min-packet-size", "obfs_min_packet_size");
        int? maxPacketSize = ReadPacketSize(node, "obfs-max-packet-size", "obfs_max_packet_size");
        if (obfsType != "gecko" && node.Properties.Any(property =>
                property.Key is "obfs-min-packet-size" or "obfs_min_packet_size"
                    or "obfs-max-packet-size" or "obfs_max_packet_size"))
            throw new NodeParseException("字段 'obfs-min-packet-size/obfs-max-packet-size' 只适用于 gecko");
        if ((minPacketSize ?? 512) > (maxPacketSize ?? 1200))
            throw new NodeParseException("Gecko min_packet_size 不能大于 max_packet_size（缺失值为 512/1200）");
        if (obfsType != null)
        {
            obfsConfig = new OutboundObfs
            {
                Type = obfsType,
                Password = node.GetRawString("obfs-password") is { Length: > 0 } password
                    ? password : throw new NodeParseException("字段 'obfs-password' 不能为空"),
                MinPacketSize = minPacketSize is 0 ? null : minPacketSize,
                MaxPacketSize = maxPacketSize is 0 ? null : maxPacketSize
            };
        }

        var (hopInterval, hopIntervalMax) = ParseHopInterval(node);
        string? bbr = node.GetRawString("bbr-profile") ?? node.GetRawString("bbr_profile");
        if (bbr is not null && bbr is not ("standard" or "conservative" or "aggressive"))
            throw new NodeParseException("字段 'bbr-profile' 只支持 standard、conservative 或 aggressive");
        return new Hysteria2Outbound
        {
            Tag = name,
            Server = server,
            ServerPort = serverPort,
            ServerPorts = serverPorts,
            UpMbps = ParseBandwidth(node, "up"),
            DownMbps = ParseBandwidth(node, "down"),
            HopInterval = hopInterval,
            HopIntervalMax = hopIntervalMax,
            BbrProfile = bbr,
            Obfs = obfsConfig,
            Password = node.GetRequiredString("password"),
            // Hysteria2 使用 QUIC，而 sing-box 的 QUIC 自定义 TLS 不支持 uTLS。
            Tls = TlsConfigHelper.Extract(
                node,
                server,
                forceTls: true,
                supportsUtls: false)
        };
    }

    private static int? ReadPacketSize(ClashProxyNode node, string field, string alias)
    {
        object? value = node.GetValue(field) ?? node.GetValue(alias);
        if (value is null) return null;
        if (!int.TryParse(value.ToString(), out int size) || size is < 0 or > 2048)
            throw new NodeParseException($"字段 '{field}' 必须是 0..2048 的整数（0 使用核心默认值）");
        return size == 0 ? null : size;
    }

    private static int? ParseBandwidth(ClashProxyNode node, string field)
    {
        string? value = node.GetRawString(field);
        if (value is null) return null;
        if (Regex.IsMatch(value, "\\A\\+?[0-9]+\\z", RegexOptions.CultureInvariant)) value = value.TrimStart('+') + " Mbps";
        Match match = Regex.Match(value, "\\A([0-9]+)[ \\t\\r\\n\\f]*([KMGT]?)([Bb])ps\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, out ulong quantity))
            throw new NodeParseException($"字段 '{field}' 带宽格式无效；必须符合 Mihomo 的整数单位语法");
        ulong multiplier = match.Groups[2].Value switch
        {
            "K" => 1_000,
            "M" => 1_000_000,
            "G" => 1_000_000_000,
            "T" => 1_000_000_000_000,
            _ => 1
        };
        if (quantity > ulong.MaxValue / multiplier)
            throw new NodeParseException($"字段 '{field}' 带宽超出源解析器范围");
        ulong amount = quantity * multiplier;
        // Mihomo stores bytes/s; sing-box accepts integral megabits/s only.
        ulong divisor = match.Groups[3].Value == "B" ? 125_000UL : 1_000_000UL;
        if (amount % divisor != 0 || amount / divisor > int.MaxValue)
            throw new NodeParseException($"字段 '{field}' 无法精确映射为整数 Mbps（不得舍入或截断）");
        return (int)(amount / divisor);
    }

    private static (string?, string?) ParseHopInterval(ClashProxyNode node)
    {
        string? value = node.GetRawString("hop-interval") ?? node.GetRawString("hop_interval");
        if (value is null) return (null, null);
        string[] parts = value.Trim().Split('-').Select(part => part.Trim('[', ' ', ']')).ToArray();
        if (value.Trim().Length == 0) return ("30s", null);
        if (parts.Length is < 1 or > 2 || parts.Any(part =>
                !ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new NodeParseException("字段 'hop-interval' 只支持 Mihomo 的整数秒数或 min-max 范围");
        ulong start = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
        ulong end = parts.Length == 2 ? ulong.Parse(parts[1], CultureInfo.InvariantCulture) : start;
        if (start > end) (start, end) = (end, start);
        start = start == 0 ? 30 : Math.Max(start, 5);
        end = Math.Max(end, start);
        if (end > long.MaxValue / 1_000_000_000)
            throw new NodeParseException("字段 'hop-interval' 超出 Go duration 范围");
        return ($"{start}s", parts.Length == 2 ? $"{end}s" : null);
    }

    private static (int? ServerPort, List<string>? ServerPorts) ParsePorts(
        ClashProxyNode node)
    {
        string? portsValue = node.GetString("ports");
        if (portsValue == null)
        {
            return (node.GetRequiredInt("port"), null);
        }

        string[] entries = portsValue.Split(
            ',',
            StringSplitOptions.TrimEntries);
        if (entries.Length == 0 || entries.Any(string.IsNullOrWhiteSpace))
        {
            throw new NodeParseException("ports 包含空端口项");
        }

        var normalizedEntries = entries
            .Select(NormalizePortEntry)
            .ToList();

        if (normalizedEntries.Count == 1
            && !normalizedEntries[0].Contains(':'))
        {
            return (ParsePort(normalizedEntries[0], "ports"), null);
        }

        return (null, normalizedEntries);
    }

    private static string NormalizePortEntry(string entry)
    {
        string[] range = entry.Split(
            '-',
            StringSplitOptions.TrimEntries);
        if (range.Length == 1)
        {
            return ParsePort(range[0], "ports").ToString(CultureInfo.InvariantCulture);
        }

        if (range.Length != 2
            || string.IsNullOrWhiteSpace(range[0])
            || string.IsNullOrWhiteSpace(range[1]))
        {
            throw new NodeParseException($"ports 中的端口范围格式无效: {entry}");
        }

        int start = ParsePort(range[0], "ports");
        int end = ParsePort(range[1], "ports");
        if (start > end)
        {
            throw new NodeParseException($"ports 中的端口范围起点不能大于终点: {entry}");
        }

        return $"{start}:{end}";
    }

    private static int ParsePort(string value, string fieldName)
    {
        if (int.TryParse(value, out int port)
            && port is > 0 and <= 65535)
        {
            return port;
        }

        throw new NodeParseException(
            $"{fieldName} 包含无效端口 '{value}'，端口必须为 1-65535 的整数");
    }
}
