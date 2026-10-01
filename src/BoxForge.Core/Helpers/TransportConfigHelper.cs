using System.Globalization;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;

namespace BoxForge.Helpers;

internal static class TransportConfigHelper
{
    private static ClashObject? Options(ClashProxyNode node, string name) =>
        node.GetObject(name) ?? node.GetObject(name.Replace('-', '_'));

    public static string TlsServerName(ClashProxyNode node, string server)
    {
        // Mihomo VLESS WS uses Host as its SNI fallback; Trojan uses sni/server.
        if (IsVless(node) && node.GetString("network") == "ws")
        {
            return Host(Options(node, "ws-opts")?.GetObject("headers")) ?? server;
        }
        return server;
    }

    public static V2RayTransport? Extract(ClashProxyNode node, string server, OutboundTls? tls) =>
        node.GetString("network") switch
        {
            "grpc" => ExtractGrpc(node, tls),
            "ws" => ExtractWebSocket(node, server, tls),
            _ => null
        };

    private static GrpcTransport ExtractGrpc(ClashProxyNode node, OutboundTls? tls)
    {
        // The pinned official core uses grpc-lite: it appends the server
        // port to TLS server_name for :authority. Mihomo keeps an explicit
        // SNI verbatim, and there is no independent target authority field.
        if (node.GetValue("sni") is not null || node.GetValue("servername") is not null)
            throw new NodeParseException("字段 'sni/servername' 与 gRPC 组合的 HTTP authority 无法精确映射到目标 grpc-lite；仅支持来源默认 server:port authority");
        ValidateTransportAlpn(tls, "h2", enforceProtocol: true);
        ClashObject? options = Options(node, "grpc-opts");
        string? service = RawString(options, "grpc-service-name", "grpc-opts");
        if (string.IsNullOrEmpty(service) || service.StartsWith('/')
            || service.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '-')))
        {
            throw new NodeParseException("字段 'grpc-opts.grpc-service-name' 必须是非空普通服务名；省略值和自定义完整 RPC path 的两端语义不同");
        }
        return new GrpcTransport { ServiceName = service };
    }

    private static V2RayTransport ExtractWebSocket(ClashProxyNode node, string server, OutboundTls? tls)
    {
        if (tls?.Reality is not null)
        {
            throw new NodeParseException("字段 'reality-opts' 与 network=ws 不能精确组合：Mihomo WS 分支不应用 Reality，不能转换为 sing-box Reality 握手");
        }
        ValidateTransportAlpn(tls, "http/1.1",
            enforceProtocol: IsVless(node) || tls?.Utls is not null);
        ClashObject? ws = Options(node, "ws-opts");
        string? path = RawString(ws, "path");
        uint? earlyData = EarlyData(ws);
        string? earlyHeader = RawString(ws, "early-data-header-name");
        bool upgrade = Boolean(ws, "v2ray-http-upgrade");

        if (earlyData > 0 && earlyHeader is { Length: > 0 })
        {
            if (!IsHeaderName(earlyHeader) || IsHandshakeHeader(earlyHeader)
                || earlyHeader.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw new NodeParseException("字段 'ws-opts.early-data-header-name' 必须是非保留的合法 HTTP header 名称");
        }

        // Mihomo parses path as a URL (including query/fragment/ed); sing-box
        // treats it as an escaped path. Reject those forms rather than copy them.
        if (path is not null && !IsOrdinaryPath(path))
        {
            throw new NodeParseException("字段 'ws-opts.path' 的 URL authority/scheme/query/fragment/escape（含 ?ed=）尚未精确映射，请使用普通路径和显式 early-data 字段");
        }

        ClashObject? sourceHeaders = ws?.GetObject("headers");
        var headers = Headers(sourceHeaders);
        string? explicitHost = Host(sourceHeaders);
        if (IsVless(node) && tls is null && explicitHost is null)
        {
            throw new NodeParseException("字段 'ws-opts.headers.Host' 在无 TLS 的 VLESS WS 中必须显式指定：Mihomo 随机 Host 无法精确映射");
        }
        string host = explicitHost ?? (IsVless(node) ? server : tls!.ServerName);

        if (upgrade)
        {
            if (earlyData > 0 || !string.IsNullOrEmpty(earlyHeader))
            {
                throw new NodeParseException("字段 'ws-opts.max-early-data/early-data-header-name' 与 HTTPUpgrade 组合没有等价目标字段");
            }
            foreach (string key in headers.Keys.Where(key => key.Equals("Host", StringComparison.OrdinalIgnoreCase)).ToArray())
                headers.Remove(key);
            return new HttpUpgradeTransport
            {
                Host = host,
                Path = path,
                Headers = headers.Count == 0 ? null : headers
            };
        }
        // Materialize the source's effective Host, not a BoxForge tuning value.
        if (explicitHost is null) headers.Add("Host", host);
        return new WebSocketTransport
        {
            Path = path,
            Headers = headers,
            MaxEarlyData = earlyData,
            EarlyDataHeaderName = earlyHeader
        };
    }

    private static bool IsVless(ClashProxyNode node) =>
        string.Equals(node.Type, "vless", StringComparison.OrdinalIgnoreCase);

    internal static string? ValidateHeaders(object? value)
    {
        if (value is not ClashObject headers) return "必须是 header 对象";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, object? raw) in headers.Properties.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!names.Add(name)) return $"包含大小写重复 header '{name}'，源运行时顺序不确定";
            if (raw is not string text) return $"包含非字符串 header '{name}'；不允许将列表或对象扁平化";
            if (!IsHeaderName(name) || text.Any(character => char.IsAscii(character)
                    && char.IsControl(character) && character != '\t'))
                return $"包含无效 HTTP header '{name}'";
            if (IsHandshakeHeader(name))
                return $"包含握手保留 header '{name}'，两端覆盖行为不同";
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(text))
                return "Host 必须是非空字符串";
        }
        return null;
    }

    private static bool IsHeaderName(string name) => name.Length > 0
        && name.All(character => char.IsAsciiLetterOrDigit(character)
            || "!#$%&'*+-.^_`|~".Contains(character));

    private static bool IsOrdinaryPath(string path)
    {
        if (path.IndexOfAny(['?', '#', '%']) >= 0 || path.StartsWith("//", StringComparison.Ordinal)) return false;
        int colon = path.IndexOf(':');
        int slash = path.IndexOf('/');
        return colon < 0 || (slash >= 0 && slash < colon);
    }

    private static bool IsHandshakeHeader(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> Headers(ClashObject? source) =>
        source?.Properties.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static string? Host(ClashObject? headers) =>
        headers?.Properties.FirstOrDefault(entry => entry.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)).Value as string;

    private static string? RawString(ClashObject? options, string name, string prefix = "ws-opts")
    {
        object? value = options?.GetValue(name) ?? options?.GetValue(name.Replace('-', '_'));
        return value switch
        {
            null => null,
            string text => text,
            _ => throw new NodeParseException($"字段 '{prefix}.{name}' 必须是字符串")
        };
    }

    private static bool Boolean(ClashObject? options, string name)
    {
        object? value = options?.GetValue(name) ?? options?.GetValue(name.Replace('-', '_'));
        if (value is null) return false;
        return bool.TryParse(value.ToString(), out bool enabled) ? enabled
            : throw new NodeParseException($"字段 'ws-opts.{name}' 必须是 true 或 false");
    }

    private static uint? EarlyData(ClashObject? options)
    {
        object? value = options?.GetValue("max-early-data") ?? options?.GetValue("max_early_data");
        if (value is null) return null;
        if (uint.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint size)
            && size <= int.MaxValue) return size;
        throw new NodeParseException("字段 'ws-opts.max-early-data' 必须是 0 到 2147483647 的整数");
    }

    private static void ValidateTransportAlpn(OutboundTls? tls, string protocol, bool enforceProtocol)
    {
        if (tls?.Alpn is not { } alpn) return;
        if (alpn.Count == 0 || (enforceProtocol && (alpn.Count != 1 || alpn[0] != protocol)))
            throw new NodeParseException($"字段 'alpn' 与该 transport 的 Mihomo 强制/空值行为不等价；此组合必须显式指定 [{protocol}]");
    }
}
