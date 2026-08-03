using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public class Hysteria2Converter : IProxyConverter
{
    public bool CanHandle(string proxyType) => proxyType == "hysteria2";

    public NodeConversionResult Convert(ClashProxyNode node)
    {
        string name = node.GetString("name") ?? "Unknown-HY2-Node";

        try
        {
            string server = node.GetRequiredString("server");
            var (serverPort, serverPorts) = ParsePorts(node);

            // 组装混淆配置 (可选)
            OutboundObfs? obfsConfig = null;
            string? obfsType = node.GetString("obfs");
            if (obfsType != null)
            {
                obfsConfig = new OutboundObfs { Type = obfsType, Password = node.GetString("obfs-password") };
            }

            return NodeConversionResult.Success(new Hysteria2Outbound
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
            });
        }
        catch (NodeParseException ex)
        {
            return NodeConversionResult.Fail($"Hysteria2 节点 '{name}' 解析失败 -> {ex.Message}");
        }
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
            return ParsePort(range[0], "ports").ToString();
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
