using BoxForge.Helpers;
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
    internal static AnyTlsNode Parse(NodeFieldReader node, string name)
    {
        string server = node.RequiredString("server");
        int port = node.RequiredPort("port");
        string password = node.RequiredRawString("password");
        var timeout = node.OptionalDuration("idle-session-timeout");
        var interval = node.OptionalDuration("idle-session-check-interval");
        int? minimumIdle = node.OptionalInt("min-idle-session", 0);
        string? metadata = ExtractMetadata(node);
        var tls = TlsConfigHelper.ExtractSettings(node, server, forceTls: true)
            ?? throw new InvalidOperationException("AnyTLS interpretation unexpectedly returned no TLS settings.");
        return new AnyTlsNode(name, server, port, password, timeout, interval,
            minimumIdle, metadata,
            new(node.OptionalBool("tls"), node.PresentField("tls") is not null), tls);
    }

    private static string? ExtractMetadata(NodeFieldReader node)
    {
        string field = node.PresentField("client-metadata") ?? "client-metadata";
        object? value = node.Value(field);
        if (value is null) return null;
        if (value is string metadata && AnyTlsNode.IsValidMetadata(metadata)) return metadata;
        throw node.Error(field, "字段 'client-metadata' 必须是无换行的字符串，UTF-8 长度不能超过 65479 字节");
    }
}
