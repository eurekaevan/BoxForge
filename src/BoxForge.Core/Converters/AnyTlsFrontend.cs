using System.Globalization;
using BoxForge.Exceptions;
using BoxForge.Helpers;
using BoxForge.Models.Clash;
using BoxForge.Models.Intermediate;

namespace BoxForge.Converters;

internal static class AnyTlsFrontend
{
    private static readonly string[] IdleTimeoutAliases = ["idle_session_timeout", "idle-timeout", "idle_timeout"];
    private static readonly string[] IdleCheckIntervalAliases = ["idle_session_check_interval"];

    internal static SourceFieldSchema Schema { get; } = SourceFieldSchemas.Common()
        .Include(SourceFieldSchemas.Tls(supportsReality: false, supportsUtls: true, forceTls: true))
        .Mapped("password", AnyTlsSemantics.IdleTimeout.SourceField)
        .Alias(AnyTlsSemantics.IdleTimeout.SourceField, IdleTimeoutAliases)
        .Mapped(AnyTlsSemantics.IdleCheckInterval.SourceField,
            AnyTlsSemantics.MinimumIdleSessions.SourceField, AnyTlsSemantics.ClientMetadata.SourceField)
        .Alias(AnyTlsSemantics.IdleCheckInterval.SourceField, IdleCheckIntervalAliases)
        .Alias(AnyTlsSemantics.MinimumIdleSessions.SourceField, "min_idle_session")
        .Alias(AnyTlsSemantics.ClientMetadata.SourceField, "client_metadata")
        .Unsupported("目标 outbound 未暴露 disable_reuse", "disable-reuse", "disable_reuse");

    // Preserve the original order: server, port, password, sessions, metadata, TLS.
    // The adapter's base class has already checked the name and Schema.
    internal static AnyTlsNode ParseValidated(ClashProxyNode node, string name)
    {
        string server = node.GetRequiredString("server");
        int port = node.GetRequiredInt("port");
        string password = node.GetRequiredRawString("password");
        var timeout = ExtractDuration(node, AnyTlsSemantics.IdleTimeout, IdleTimeoutAliases);
        var interval = ExtractDuration(node, AnyTlsSemantics.IdleCheckInterval, IdleCheckIntervalAliases);
        int? minimumIdle = ExtractMinIdleSession(node);
        string? metadata = ExtractMetadata(node);
        var tls = TlsConfigHelper.ExtractSettings(node, server, forceTls: true)
            ?? throw new InvalidOperationException("AnyTLS interpretation unexpectedly returned no TLS settings.");
        return new AnyTlsNode(name, server, port, password, timeout, interval,
            new(minimumIdle, node.GetPresentField("min-idle-session", "min_idle_session"), AnyTlsSemantics.MinimumIdleSessions.Id),
            new(metadata, node.GetPresentField("client-metadata", "client_metadata"), AnyTlsSemantics.ClientMetadata.Id),
            new(node.GetNullableBool("tls"), node.GetPresentField("tls"), AnyTlsSemantics.Tls.Id), tls);
    }

    private static SourceValue<string?> ExtractDuration(ClashProxyNode node, SemanticMapping rule, string[] aliases)
    {
        string? field = node.GetPresentField(rule.SourceField)
            ?? aliases.FirstOrDefault(alias => node.GetPresentField(alias) is not null);
        string? value = field is null ? null : node.GetRawString(field);
        return new(value is null ? null : GoDurationHelper.ParsePositiveSecondsOrDuration(value, field!), field, rule.Id);
    }

    private static int? ExtractMinIdleSession(ClashProxyNode node)
    {
        object? value = node.GetValue("min-idle-session") ?? node.GetValue("min_idle_session");
        if (value is null) return null;
        if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count >= 0) return count;
        throw new NodeParseException("字段 'min-idle-session' 必须是 0..2147483647 的整数");
    }

    private static string? ExtractMetadata(ClashProxyNode node)
    {
        object? value = node.GetValue("client-metadata") ?? node.GetValue("client_metadata");
        if (value is null) return null;
        if (value is string metadata && AnyTlsNode.IsValidMetadata(metadata)) return metadata;
        throw new NodeParseException("字段 'client-metadata' 必须是无换行的字符串，UTF-8 长度不能超过 65479 字节");
    }
}
