using System.Collections;
using System.Text.Json.Serialization.Metadata;
using BoxForge.Models.Singbox;

namespace BoxForge.Services;

// Output-only defaults for the qualified sing-box 1.15.0-alpha.8 core. Keep the complete
// model for validation and planning; never confuse CLR zeroes with core defaults.
internal static class SingboxJsonDefaults
{
    public static SingboxConfig NormalizeContext(SingboxConfig config)
    {
        string? defaultHttpClient = string.IsNullOrEmpty(config.Route.DefaultHttpClient)
            ? config.HttpClients.FirstOrDefault()?.Tag : config.Route.DefaultHttpClient;
        return config with
        {
            Outbounds = config.Outbounds.Select(outbound => outbound switch
            {
                VlessOutbound node => node with
                {
                    Tls = NormalizeTls(node.Tls, node.Server, TransportAlpn(node.Transport)),
                    Transport = NormalizeTransport(node.Transport, node.Tls, node.Server)
                },
                TrojanOutbound node => node with
                {
                    Tls = NormalizeTls(node.Tls, node.Server, TransportAlpn(node.Transport)),
                    Transport = NormalizeTransport(node.Transport, node.Tls, node.Server)
                },
                Hysteria2Outbound node => node with
                {
                    Tls = NormalizeTls(node.Tls, node.Server, "h3"),
                    HopIntervalMax = EqualDurations(node.HopInterval, node.HopIntervalMax) ? null : node.HopIntervalMax
                },
                AnyTlsOutbound node => node with { Tls = NormalizeTls(node.Tls, node.Server) },
                _ => outbound
            }).ToList(),
            Dns = config.Dns with
            {
                Final = config.Dns.Final == config.Dns.Servers.FirstOrDefault()?.Tag ? "" : config.Dns.Final,
                Servers = config.Dns.Servers.Select(server => server is HttpsDnsServer https
                    && https.TlsConfig.ServerName == https.ServerAddress
                    ? https with { TlsConfig = https.TlsConfig with { ServerName = "" } } : server).ToList(),
                Rules = config.Dns.Rules.Select(rule => MatchesDnsTimeout(rule.Timeout, config.Dns.Timeout)
                    ? rule with { Timeout = null } : rule).ToList()
            },
            Route = config.Route with
            {
                Final = config.Route.Final == config.Outbounds.FirstOrDefault()?.Tag ? null : config.Route.Final,
                DefaultHttpClient = config.Route.DefaultHttpClient == config.HttpClients.FirstOrDefault()?.Tag
                    ? null : config.Route.DefaultHttpClient,
                RuleSet = config.Route.RuleSet.Select(ruleSet => ruleSet.HttpClient == defaultHttpClient
                    ? ruleSet with { HttpClient = null } : ruleSet).ToList()
            }
        };
    }

    public static void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type.Namespace != typeof(SingboxConfig).Namespace) return;
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            string name = property.Name;
            property.ShouldSerialize = (owner, value) => !IsDefault(owner, name, value);
        }
    }

    private static OutboundTls? NormalizeTls(OutboundTls? tls, string server, string? defaultAlpn = null) =>
        tls is null ? null : tls with
        {
            ServerName = tls.ServerName == server ? "" : tls.ServerName,
            Alpn = defaultAlpn is not null && tls.Alpn is { Count: 1 } && tls.Alpn[0] == defaultAlpn ? null : tls.Alpn
        };

    private static string? TransportAlpn(V2RayTransport? transport) => transport switch
    {
        WebSocketTransport or HttpUpgradeTransport => "http/1.1",
        GrpcTransport => "h2",
        _ => null
    };

    private static V2RayTransport? NormalizeTransport(V2RayTransport? transport, OutboundTls? tls, string server) =>
        transport is HttpUpgradeTransport upgrade && tls is { Enabled: true }
        && upgrade.Host == (string.IsNullOrEmpty(tls.ServerName) ? server : tls.ServerName)
            ? upgrade with { Host = "" } : transport;

    private static bool IsDefault(object owner, string name, object? value)
    {
        if (value is null) return true;
        if (value is false && HasFalseDefault(owner, name)) return true;
        if (value is ICollection { Count: 0 }) return true;

        return (owner, name) switch
        {
            (ProxyOutbound, "connect_timeout") => Duration(value, 5),
            (ProxyOutbound, "tcp_keep_alive") => Duration(value, 300),
            (ProxyOutbound, "tcp_keep_alive_interval") => Duration(value, 75),
            (SelectorOutbound selector, "default") => Equals(value, selector.Outbounds.FirstOrDefault()),
            (UrlTestOutbound, "url") => value is "" or "https://www.gstatic.com/generate_204",
            (UrlTestOutbound, "interval") => Duration(value, 180),
            (UrlTestOutbound, "tolerance") => value is 0 or 50,
            (UrlTestOutbound, "idle_timeout") => Duration(value, 1800),
            (VlessOutbound, "packet_encoding") => Equals(value, "xudp"),
            (UdpOverTcpOptions, "version") => value is 0 or 2,
            (AnyTlsOutbound, "idle_session_timeout" or "idle_session_check_interval") => Duration(value, 30),
            (AnyTlsOutbound, "min_idle_session") => Equals(value, 0),
            // A range requires both bounds; the core only defaults a fixed interval.
            (Hysteria2Outbound node, "hop_interval") => IsZeroOrAbsentDuration(node.HopIntervalMax) && Duration(value, 30),
            (Hysteria2Outbound, "hop_interval_max") => IsZeroOrAbsentDuration(value as string),
            (Hysteria2Outbound, "bbr_profile") => value is "" or "standard",
            (Hysteria2Outbound, "up_mbps" or "down_mbps") => Equals(value, 0),
            (OutboundObfs { Type: "gecko" }, "min_packet_size") => value is 0 or 512,
            (OutboundObfs { Type: "gecko" }, "max_packet_size") => value is 0 or 1200,
            (Utls, "fingerprint") => value is "" or "chrome",
            (DnsTlsConfig, "enabled") => true, // HTTPS DNS unconditionally enables TLS.
            (Inbound { Type: "tun" }, "dns_mode") => value is "" or "hijack",
            (WebSocketTransport, "max_early_data") => Equals(value, 0u),
            (WebSocketTransport or HttpUpgradeTransport, "path") => Equals(value, "") || Equals(value, "/"),
            (DnsOptimisticConfig, "timeout") => Duration(value, 259200),
            (DnsConfig, "timeout") => Duration(value, 10),
            (DnsConfig, "cache_capacity") => value is uint capacity && capacity <= 1024,
            (DnsRule, "action") => Equals(value, DnsRuleAction.Route),
            (DnsRule { Action: DnsRuleAction.Predefined }, "rcode") => Equals(value, DnsResponseCode.NoError),
            (RouteRule, "action") => Equals(value, RouteRuleAction.Route),
            (RouteRule { Action: RouteRuleAction.Sniff }, "timeout") => Duration(value, 0.3m),
            (SingboxRuleSet ruleSet, "format") => value is RuleSetFormat.Binary && HasBinaryExtension(ruleSet.Url),
            (SingboxRuleSet, "update_interval") => Duration(value, 86400),
            (CacheFileConfig, "path") => value is "" or "cache.db",
            (LogConfig, "level") => value is "" or "trace",
            (_, "flow" or "client_metadata" or "short_id" or "server_name" or "path"
                or "early_data_header_name" or "detour" or "cache_id" or "final" or "default"
                or "default_http_client" or "http_client" or "host") => Equals(value, ""),
            _ => false
        };
    }

    private static bool HasFalseDefault(object owner, string name) => (owner, name) switch
    {
        (Inbound, "auto_route" or "strict_route" or "auto_redirect") => true,
        (TailscaleEndpoint, "accept_routes" or "on_demand") => true,
        (ApiService, "access_control_allow_private_network") => true,
        (ApiDashboardConfig or DnsOptimisticConfig or UdpOverTcpOptions or Utls or OutboundReality, "enabled") => true,
        (OutboundTls, "enabled" or "insecure") => true,
        (SelectorOutbound or UrlTestOutbound, "interrupt_exist_connections") => true,
        (DnsConfig, "reverse_mapping") => true,
        (DnsResolverOptions, "disable_optimistic_cache") => true,
        (DnsServer, "accept_default_resolvers") => true,
        (DnsRule, "ip_accept_any" or "race" or "speculative" or "disable_optimistic_cache") => true,
        (RouteRule, "ip_is_private" or "invert" or "no_drop") => true,
        (RouteConfig, "auto_detect_interface") => true,
        (CacheFileConfig, "enabled" or "store_dns") => true,
        (LogConfig, "timestamp") => true,
        _ => false
    };

    private static bool HasBinaryExtension(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && Path.GetExtension(uri.AbsolutePath) == ".srs";

    private static bool Duration(object value, decimal seconds) =>
        value is string text && Helpers.GoDurationHelper.TryParseDuration(text, out long nanoseconds)
        && (nanoseconds == 0 || nanoseconds == seconds * 1_000_000_000);

    private static bool MatchesDnsTimeout(object? value, string globalTimeout) => value is string text
        && Helpers.GoDurationHelper.TryParseDuration(text, out long nanoseconds)
        && Helpers.GoDurationHelper.TryParseDuration(globalTimeout, out long globalNanoseconds)
        && (nanoseconds == 0 || nanoseconds == (globalNanoseconds == 0 ? 10_000_000_000 : globalNanoseconds));

    private static bool IsZeroOrAbsentDuration(string? value) => value is null
        || Helpers.GoDurationHelper.TryParseDuration(value, out long nanoseconds) && nanoseconds == 0;

    private static bool EqualDurations(string? left, string? right) => left is not null && right is not null
        && Helpers.GoDurationHelper.TryParseDuration(left, out long leftNanoseconds)
        && Helpers.GoDurationHelper.TryParseDuration(right, out long rightNanoseconds)
        && leftNanoseconds == rightNanoseconds;
}
