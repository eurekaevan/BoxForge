using System.Text;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public sealed class AnyTlsConverter()
    : ProxyConverterBase("AnyTLS", "anytls")
{
    private static readonly SourceFieldSchema SourceFields = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: false, supportsUtls: true, forceTls: true))
        .Mapped("password", "idle-session-timeout")
        .Alias("idle-session-timeout",
            "idle_session_timeout", "idle-timeout", "idle_timeout")
        .Mapped("idle-session-check-interval", "min-idle-session", "client-metadata")
        .Alias("idle-session-check-interval", "idle_session_check_interval")
        .Alias("min-idle-session", "min_idle_session")
        .Alias("client-metadata", "client_metadata")
        .Unsupported("目标 outbound 未暴露 disable_reuse", "disable-reuse", "disable_reuse");

    protected override SourceFieldSchema Schema => SourceFields;

    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");

        return new AnyTlsOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = node.GetRequiredInt("port"),
            Password = node.GetRequiredString("password"),
            IdleSessionTimeout = ExtractDuration(node, "idle-session-timeout",
                "idle_session_timeout", "idle-timeout", "idle_timeout"),
            IdleSessionCheckInterval = ExtractDuration(node, "idle-session-check-interval",
                "idle_session_check_interval"),
            MinIdleSession = ExtractMinIdleSession(node),
            ClientMetadata = ExtractMetadata(node),
            Tls = TlsConfigHelper.Extract(node, server, forceTls: true)
        };
    }

    private static string? ExtractDuration(ClashProxyNode node, params string[] fields)
    {
        string? field = fields.FirstOrDefault(name => node.Properties.Any(property => property.Key == name));
        if (field is null) return null;
        string? value = node.GetRawString(field);
        return value is null ? null : GoDurationHelper.ParsePositiveSecondsOrDuration(value, field);
    }

    private static int? ExtractMinIdleSession(ClashProxyNode node)
    {
        object? value = node.GetValue("min-idle-session") ?? node.GetValue("min_idle_session");
        if (value is null) return null;
        if (int.TryParse(value?.ToString(), out int count) && count >= 0) return count;
        throw new NodeParseException("字段 'min-idle-session' 必须是 0..2147483647 的整数");
    }

    private static string? ExtractMetadata(ClashProxyNode node)
    {
        object? value = node.GetValue("client-metadata") ?? node.GetValue("client_metadata");
        if (value is null) return null;
        // The unescaped newline-delimited settings frame has a uint16 length.
        if (value is string metadata && !metadata.Contains('\n')
            && Encoding.UTF8.GetByteCount(metadata) <= ushort.MaxValue - 56)
            return metadata;
        throw new NodeParseException("字段 'client-metadata' 必须是无换行的字符串，UTF-8 长度不能超过 65479 字节");
    }
}
