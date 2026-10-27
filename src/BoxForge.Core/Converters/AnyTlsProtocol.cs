using System.Globalization;
using BoxForge.Exceptions;
using BoxForge.Helpers;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class AnyTlsProtocol
{
    private static readonly string[] IdleTimeoutAliases = ["idle_session_timeout", "idle-timeout", "idle_timeout"];
    private static readonly string[] IdleCheckIntervalAliases = ["idle_session_check_interval"];

    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: false, supportsUtls: true, forceTls: true))
        .Mapped("password", "idle-session-timeout")
        .Alias("idle-session-timeout", IdleTimeoutAliases)
        .Mapped("idle-session-check-interval",
            "min-idle-session", "client-metadata")
        .Alias("idle-session-check-interval", IdleCheckIntervalAliases)
        .Alias("min-idle-session", "min_idle_session")
        .Alias("client-metadata", "client_metadata")
        .Unsupported("目标 outbound 未暴露 disable_reuse", "disable-reuse", "disable_reuse");

    // Preserve the original order: server, port, password, sessions, metadata, TLS.
    // The parser has already checked the name and schema.
    internal static AnyTlsNode ParseValidated(ClashProxyNode node, string name)
    {
        string server = node.GetRequiredString("server");
        int port = node.GetRequiredPort("port");
        string password = node.GetRequiredRawString("password");
        var timeout = ExtractDuration(node, "idle-session-timeout", IdleTimeoutAliases);
        var interval = ExtractDuration(node, "idle-session-check-interval", IdleCheckIntervalAliases);
        int? minimumIdle = ExtractMinIdleSession(node);
        string? metadata = ExtractMetadata(node);
        var tls = TlsConfigHelper.ExtractSettings(node, server, forceTls: true)
            ?? throw new InvalidOperationException("AnyTLS interpretation unexpectedly returned no TLS settings.");
        return new AnyTlsNode(name, server, port, password, timeout, interval,
            minimumIdle, metadata,
            new(node.GetNullableBool("tls"), node.GetPresentField("tls") is not null), tls);
    }

    private static string? ExtractDuration(ClashProxyNode node, string name, string[] aliases)
    {
        string? field = node.GetPresentField(name)
            ?? aliases.FirstOrDefault(alias => node.GetPresentField(alias) is not null);
        string? value = field is null ? null : node.GetRawString(field);
        return value is null ? null : GoDurationHelper.ParsePositiveSecondsOrDuration(value, field!);
    }

    private static int? ExtractMinIdleSession(ClashProxyNode node)
    {
        string field = node.GetPresentField("min-idle-session", "min_idle_session") ?? "min-idle-session";
        object? value = node.GetValue(field);
        if (value is null) return null;
        if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count >= 0) return count;
        throw NodeParseException.ForField(field, "字段 'min-idle-session' 必须是 0..2147483647 的整数");
    }

    private static string? ExtractMetadata(ClashProxyNode node)
    {
        string field = node.GetPresentField("client-metadata", "client_metadata") ?? "client-metadata";
        object? value = node.GetValue(field);
        if (value is null) return null;
        if (value is string metadata && AnyTlsNode.IsValidMetadata(metadata)) return metadata;
        throw NodeParseException.ForField(field, "字段 'client-metadata' 必须是无换行的字符串，UTF-8 长度不能超过 65479 字节");
    }
}
