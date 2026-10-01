using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;
using System.Globalization;

namespace BoxForge.Converters;

public sealed class Hysteria2Converter()
    : ProxyConverterBase("Hysteria2", "hysteria2")
{
    private static readonly SourceFieldSchema SourceFields = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: false, supportsUtls: false, forceTls: true))
        .Mapped("ports", "password")
        .Conditional("obfs", SourceFieldDisposition.Mapped,
            (_, value) => string.IsNullOrWhiteSpace(value?.ToString())
                || value?.ToString() == "salamander"
                    ? null
                    : "仅 salamander 的现有映射可安全使用；gecko 及其他类型尚未映射")
        .Conditional("obfs-password", SourceFieldDisposition.Mapped,
            (node, _) => node.GetString("obfs") is null
                ? "指定了混淆密码，但未启用混淆"
                : null)
        .Unsupported("带宽设定尚未映射，不能改用 BoxForge BBR 调优", "up", "down")
        .Unsupported("显式跳端口间隔尚未映射，不能覆盖为 BoxForge 调优",
            "hop-interval", "hop_interval")
        .Unsupported("显式 BBR 配置尚未映射，不能覆盖为 BoxForge 调优",
            "bbr-profile", "bbr_profile")
        .Unsupported("启用网络类型尚未映射", "network")
        .Unsupported("Gecko 包长度尚未映射",
            "obfs-min-packet-size", "obfs-max-packet-size",
            "obfs_min_packet_size", "obfs_max_packet_size")
        .Unsupported("QUIC 或 Realm 选项尚未映射",
            "realm-opts", "realm_opts", "handshake-timeout", "handshake_timeout",
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
        if (obfsType != null)
        {
            obfsConfig = new OutboundObfs
            {
                Type = obfsType,
                Password = node.GetRequiredString("obfs-password")
            };
        }

        return new Hysteria2Outbound
        {
            Tag = name,
            Server = server,
            ServerPort = serverPort,
            ServerPorts = serverPorts,
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
