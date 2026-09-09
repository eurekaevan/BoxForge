using System.Net;
using System.Net.Sockets;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Services;

public interface ISingboxConfigValidator
{
    void Validate(SingboxConfig config);
}

/// <summary>
/// Validates BoxForge-specific generation invariants that are not rejected by
/// <c>sing-box check</c>. Structural and schema validation belongs to sing-box.
/// </summary>
public sealed class SingboxConfigValidator : ISingboxConfigValidator
{
    public void Validate(SingboxConfig config)
    {
        ValidationContext context = CreateValidationContext(config);
        ValidateTopLevelReferences(config.Route, context);
        ValidateHttpClients(config.HttpClients, context);
        ValidateServices(config.Services ?? [], context);
        ValidateOutbounds(config.Outbounds, context);
        ValidateInbounds(config.Inbounds, context.Diagnostics);
        ValidateDnsServers(config.Dns.Servers, context);
        ValidateDnsRules(config.Dns.Rules, context);
        ValidateRuleSets(config.Route.RuleSet, context);
        ValidateCacheFile(config.Experimental?.CacheFile, context.Diagnostics);
        ValidateRouteRules(config.Route.Rules, context);
        ThrowIfInvalid(context.Diagnostics);
    }

    private static ValidationContext CreateValidationContext(SingboxConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();

        var outboundTags = CollectTags(
            config.Outbounds.Select(outbound => outbound.Tag));
        ValidateRequiredTags(
            config.Outbounds.Select(outbound => outbound.Tag),
            "outbounds",
            diagnostics);

        var endpointTags = CollectTags(
            config.Endpoints?.Select(endpoint => endpoint.Tag) ?? []);
        ValidateRequiredTags(
            config.Endpoints?.Select(endpoint => endpoint.Tag) ?? [],
            "endpoints",
            diagnostics);

        var routeTargets = new HashSet<string>(
            outboundTags,
            StringComparer.Ordinal);
        routeTargets.UnionWith(endpointTags);
        var proxyOutboundTags = new HashSet<string>(
            config.Outbounds.OfType<ProxyOutbound>().Select(outbound => outbound.Tag),
            StringComparer.Ordinal);

        var dnsTags = CollectUniqueRequiredTags(
            config.Dns.Servers.Select(server => server.Tag),
            "dns.servers",
            diagnostics);
        var httpClientTags = CollectUniqueRequiredTags(
            config.HttpClients.Select(client => client.Tag),
            "http_clients",
            diagnostics);
        var ruleSetTags = CollectRuleSetTags(
            config.Route.RuleSet,
            diagnostics);
        var inboundTags = CollectTags(
            config.Inbounds.Select(inbound => inbound.Tag));
        ValidateRequiredTags(
            config.Inbounds.Select(inbound => inbound.Tag),
            "inbounds",
            diagnostics);
        CollectUniqueRequiredTags(
            config.Services?.Select(service => service.Tag) ?? [],
            "services",
            diagnostics);

        return new ValidationContext(
            diagnostics,
            routeTargets,
            proxyOutboundTags,
            endpointTags,
            dnsTags,
            httpClientTags,
            ruleSetTags,
            inboundTags);
    }

    private static void ValidateTopLevelReferences(
        RouteConfig route,
        ValidationContext context)
    {
        ValidateReference(
            route.Final,
            context.RouteTargets,
            "SB001",
            "route.final",
            "不存在对应的 outbound 或 endpoint。",
            context.Diagnostics);
        ValidateReference(
            route.DefaultHttpClient,
            context.HttpClientTags,
            "SB014",
            "route.default_http_client",
            "引用了不存在的 HTTP client。",
            context.Diagnostics);
    }

    private static void ValidateHttpClients(
        List<HttpClientConfig> clients,
        ValidationContext context)
    {
        for (var index = 0; index < clients.Count; index++)
        {
            ValidateReference(
                clients[index].Detour,
                context.RouteTargets,
                "SB015",
                $"http_clients[{index}].detour",
                "引用了不存在的 outbound 或 endpoint。",
                context.Diagnostics);
            ValidateReference(
                clients[index].DomainResolver?.Server,
                context.DnsTags,
                "SB060",
                $"http_clients[{index}].domain_resolver.server",
                "引用了不存在的 DNS server。",
                context.Diagnostics);
        }
    }

    private static void ValidateServices(
        List<SingboxService> services,
        ValidationContext context)
    {
        string[] expectedOrigins =
        [
            "http://127.0.0.1:9090",
            "http://localhost:9090"
        ];

        for (var index = 0; index < services.Count; index++)
        {
            if (services[index] is not ApiService api)
            {
                continue;
            }

            string path = $"services[{index}]";
            if (api.Listen != "127.0.0.1")
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB066",
                    $"{path}.listen",
                    "sing-box API 必须仅监听 127.0.0.1。"));
            }
            if (api.ListenPort != 9090)
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB067",
                    $"{path}.listen_port",
                    "sing-box API 监听端口必须是 9090。"));
            }
            if (!api.AccessControlAllowOrigin.SequenceEqual(
                    expectedOrigins,
                    StringComparer.Ordinal))
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB068",
                    $"{path}.access_control_allow_origin",
                    "sing-box API 只允许固定的本机 Dashboard origin。"));
            }
            if (api.AccessControlAllowPrivateNetwork)
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB069",
                    $"{path}.access_control_allow_private_network",
                    "sing-box API 不得允许浏览器私网跨域访问。"));
            }
            if (api.Dashboard == null
                || !api.Dashboard.Enabled
                || string.IsNullOrWhiteSpace(api.Dashboard.Path))
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB070",
                    $"{path}.dashboard",
                    "sing-box API Dashboard 必须启用并设置存储路径。"));
            }
            if (api.Dashboard != null)
            {
                ValidateReference(
                    api.Dashboard.HttpClient,
                    context.HttpClientTags,
                    "SB071",
                    $"{path}.dashboard.http_client",
                    "引用了不存在的 HTTP client。",
                    context.Diagnostics);
            }
        }
    }

    private static void ValidateOutbounds(
        List<Outbound> outbounds,
        ValidationContext context)
    {
        for (var index = 0; index < outbounds.Count; index++)
        {
            switch (outbounds[index])
            {
                case SelectorOutbound selector:
                    ValidateSelectorOutbound(selector, index, context);
                    break;
                case UrlTestOutbound urlTest:
                    ValidateUrlTestOutbound(urlTest, index, context);
                    break;
                case ProxyOutbound proxy:
                    ValidateProxyOutbound(proxy, index, context);
                    break;
            }
        }
    }

    private static void ValidateUrlTestOutbound(
        UrlTestOutbound urlTest,
        int index,
        ValidationContext context)
    {
        for (var childIndex = 0;
            childIndex < urlTest.Outbounds.Count;
            childIndex++)
        {
            string candidate = urlTest.Outbounds[childIndex];
            ValidateReference(
                candidate,
                context.RouteTargets,
                "SB075",
                $"outbounds[{index}].outbounds[{childIndex}]",
                "urltest 引用了不存在的目标。",
                context.Diagnostics);

            if (context.RouteTargets.Contains(candidate)
                && !context.ProxyOutboundTags.Contains(candidate))
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB076",
                    $"outbounds[{index}].outbounds[{childIndex}]",
                    "urltest 只能引用真实代理节点。"));
            }
        }

        if (urlTest.Outbounds.Count != urlTest.Outbounds.Distinct(
                StringComparer.Ordinal).Count())
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB077",
                $"outbounds[{index}].outbounds",
                "urltest 不能包含重复目标。"));
        }

        if (urlTest.Outbounds.Count < 2)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB078",
                $"outbounds[{index}].outbounds",
                "urltest 至少需要两个真实代理节点。"));
        }
    }

    private static void ValidateSelectorOutbound(
        SelectorOutbound selector,
        int index,
        ValidationContext context)
    {
        for (var childIndex = 0;
            childIndex < selector.Outbounds.Count;
            childIndex++)
        {
            ValidateReference(
                selector.Outbounds[childIndex],
                context.RouteTargets,
                "SB003",
                $"outbounds[{index}].outbounds[{childIndex}]",
                "selector 引用了不存在的目标。",
                context.Diagnostics);
        }

        if (selector.Outbounds.Count != selector.Outbounds.Distinct(
                StringComparer.Ordinal).Count())
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB017",
                $"outbounds[{index}].outbounds",
                "selector 不能包含重复目标。"));
        }

        if (selector.Default != null
            && !selector.Outbounds.Contains(
                selector.Default,
                StringComparer.Ordinal))
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB018",
                $"outbounds[{index}].default",
                "selector 默认目标不在 outbounds 中。"));
        }
    }

    private static void ValidateProxyOutbound(
        ProxyOutbound proxy,
        int index,
        ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(proxy.Server))
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB019",
                $"outbounds[{index}].server",
                "代理服务器地址不能为空。"));
        }

        bool hasValidSinglePort = proxy.ServerPort is > 0 and <= 65535;
        bool hasPortSet = proxy is Hysteria2Outbound
        {
            ServerPorts.Count: > 0
        };
        if (!hasValidSinglePort && !hasPortSet)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB020",
                $"outbounds[{index}].server_port",
                "代理节点必须配置有效端口。"));
        }

        ValidateReference(
            proxy.DomainResolver.Server,
            context.DnsTags,
            "SB004",
            $"outbounds[{index}].domain_resolver.server",
            "引用了不存在的 DNS server。",
            context.Diagnostics);
        if (proxy.DomainResolver.Strategy != DnsStrategy.Ipv4Only)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB061",
                $"outbounds[{index}].domain_resolver.strategy",
                "代理节点域名必须使用 ipv4_only 解析策略。"));
        }
        if (proxy.DomainResolver.DisableOptimisticCache != true)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB064",
                $"outbounds[{index}].domain_resolver.disable_optimistic_cache",
                "代理节点域名解析必须禁用 optimistic 过期缓存。"));
        }

        if (IPAddress.TryParse(proxy.Server, out IPAddress? serverAddress)
            && serverAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB062",
                $"outbounds[{index}].server",
                "代理节点不能使用 IPv6 字面量地址。"));
        }

        ValidateOutboundTls(proxy, index, context.Diagnostics);
        ValidateProtocolCredentials(proxy, index, context.Diagnostics);
    }

    private static void ValidateOutboundTls(
        ProxyOutbound proxy,
        int index,
        List<ConfigDiagnostic> diagnostics)
    {
        OutboundTls? tls = proxy switch
        {
            VlessOutbound vless => vless.Tls,
            TrojanOutbound trojan => trojan.Tls,
            Hysteria2Outbound hysteria2 => hysteria2.Tls,
            AnyTlsOutbound anyTls => anyTls.Tls,
            _ => null
        };
        if (tls is { Enabled: true }
            && string.IsNullOrWhiteSpace(tls.ServerName))
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB044",
                $"outbounds[{index}].tls.server_name",
                "TLS server_name 不能为空。"));
        }
    }

    private static void ValidateProtocolCredentials(
        ProxyOutbound proxy,
        int index,
        List<ConfigDiagnostic> diagnostics)
    {
        switch (proxy)
        {
            case VlessOutbound vless:
                ValidateRequired(
                    vless.Uuid,
                    "SB045",
                    $"outbounds[{index}].uuid",
                    "VLESS UUID 不能为空。",
                    diagnostics);
                break;
            case TrojanOutbound trojan:
                ValidateRequired(
                    trojan.Password,
                    "SB046",
                    $"outbounds[{index}].password",
                    "Trojan 密码不能为空。",
                    diagnostics);
                break;
            case Hysteria2Outbound hysteria2:
                ValidateRequired(
                    hysteria2.Password,
                    "SB047",
                    $"outbounds[{index}].password",
                    "Hysteria2 密码不能为空。",
                    diagnostics);
                break;
            case AnyTlsOutbound anyTls:
                ValidateRequired(
                    anyTls.Password,
                    "SB050",
                    $"outbounds[{index}].password",
                    "AnyTLS 密码不能为空。",
                    diagnostics);
                break;
        }
    }

    private static void ValidateInbounds(
        List<Inbound> inbounds,
        List<ConfigDiagnostic> diagnostics)
    {
        for (var index = 0; index < inbounds.Count; index++)
        {
            if (inbounds[index].ListenPort is int listenPort
                && listenPort is <= 0 or > 65535)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB022",
                    $"inbounds[{index}].listen_port",
                    "inbound 监听端口必须在 1-65535 之间。"));
            }
        }
    }

    private static void ValidateDnsServers(
        List<DnsServer> servers,
        ValidationContext context)
    {
        for (var index = 0; index < servers.Count; index++)
        {
            DnsServer server = servers[index];
            ValidateReference(
                server.Detour,
                context.RouteTargets,
                "SB005",
                $"dns.servers[{index}].detour",
                "引用了不存在的 outbound 或 endpoint。",
                context.Diagnostics);

            if (server is TailscaleDnsServer tailscaleServer)
            {
                ValidateReference(
                    tailscaleServer.Endpoint,
                    context.EndpointTags,
                    "SB006",
                    $"dns.servers[{index}].endpoint",
                    "引用了不存在的 Tailscale endpoint。",
                    context.Diagnostics);
            }
            else if (server is HttpsDnsServer httpsServer)
            {
                ValidateRequired(
                    httpsServer.Tls?.ServerName,
                    "SB054",
                    $"dns.servers[{index}].tls.server_name",
                    "HTTPS DNS TLS server_name 不能为空。",
                    context.Diagnostics);
            }
        }
    }

    private static void ValidateDnsRules(
        List<DnsRule> rules,
        ValidationContext context)
    {
        for (var index = 0; index < rules.Count; index++)
        {
            DnsRule rule = rules[index];
            string path = $"dns.rules[{index}]";
            if (rule.Action == null)
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB025",
                    $"{path}.action",
                    "DNS 规则动作不能为空。"));
            }

            ValidateReference(
                rule.Server,
                context.DnsTags,
                "SB007",
                $"{path}.server",
                "引用了不存在的 DNS server。",
                context.Diagnostics);

            if (rule.Action == DnsRuleAction.Evaluate
                && string.IsNullOrWhiteSpace(rule.Tag))
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB027",
                    $"{path}.tag",
                    "evaluate 响应标签不能为空。"));
            }

            if (rule.Action == DnsRuleAction.Predefined
                && !rule.Rcode.HasValue)
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB042",
                    $"{path}.rcode",
                    "predefined 动作必须指定 rcode。"));
            }

            if (rule.Race == true
                && rule.Action != DnsRuleAction.Respond)
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB031",
                    $"{path}.race",
                    "race 只允许用于 respond 动作。"));
            }
        }
    }

    private static void ValidateRuleSets(
        List<SingboxRuleSet> ruleSets,
        ValidationContext context)
    {
        for (var index = 0; index < ruleSets.Count; index++)
        {
            SingboxRuleSet ruleSet = ruleSets[index];
            if (!ruleSet.Format.HasValue)
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB056",
                    $"route.rule_set[{index}].format",
                    "rule-set format 不能为空。"));
            }

            ValidateRequired(
                ruleSet.Url,
                "SB057",
                $"route.rule_set[{index}].url",
                "远程 rule-set URL 不能为空。",
                context.Diagnostics);
            if (ruleSet.Tag is { Count: > 1 }
                && ruleSet.Url?.Contains("{tag}", StringComparison.Ordinal) != true)
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB072",
                    $"route.rule_set[{index}].url",
                    "包含多个 tag 的远程 rule-set URL 必须使用 {tag} 占位符。"));
            }
            if (ruleSet.Type == RuleSetType.Remote)
            {
                ValidateReference(
                    ruleSet.HttpClient,
                    context.HttpClientTags,
                    "SB016",
                    $"route.rule_set[{index}].http_client",
                    "引用了不存在的 HTTP client。",
                    context.Diagnostics);
            }
        }
    }

    private static void ValidateCacheFile(
        CacheFileConfig? cacheFile,
        List<ConfigDiagnostic> diagnostics)
    {
        if (cacheFile is not { Enabled: true })
        {
            return;
        }

        ValidateRequired(
            cacheFile.Path,
            "SB058",
            "experimental.cache_file.path",
            "缓存文件路径不能为空。",
            diagnostics);
        if (cacheFile.CacheId is not { Length: 64 }
            || cacheFile.CacheId.Any(character => !Uri.IsHexDigit(character)))
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB059",
                "experimental.cache_file.cache_id",
                "cache_id 必须是完整的 64 位 SHA-256 十六进制字符串。"));
        }
    }

    private static void ValidateRouteRules(
        List<RouteRule> rules,
        ValidationContext context)
    {
        for (var index = 0; index < rules.Count; index++)
        {
            ValidateRouteRule(
                rules[index],
                $"route.rules[{index}]",
                context.RouteTargets,
                context.RuleSetTags,
                context.InboundTags,
                requireAction: true,
                context.Diagnostics);
        }
    }

    private static void ThrowIfInvalid(List<ConfigDiagnostic> diagnostics)
    {
        if (diagnostics.Count > 0)
        {
            throw new ConfigValidationException(diagnostics);
        }
    }

    private sealed record ValidationContext(
        List<ConfigDiagnostic> Diagnostics,
        HashSet<string> RouteTargets,
        HashSet<string> ProxyOutboundTags,
        HashSet<string> EndpointTags,
        HashSet<string> DnsTags,
        HashSet<string> HttpClientTags,
        HashSet<string> RuleSetTags,
        HashSet<string> InboundTags);

    private static HashSet<string> CollectTags(IEnumerable<string?> tags) =>
        tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> CollectRuleSetTags(
        List<SingboxRuleSet> ruleSets,
        List<ConfigDiagnostic> diagnostics)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var ruleSetIndex = 0; ruleSetIndex < ruleSets.Count; ruleSetIndex++)
        {
            List<string>? tags = ruleSets[ruleSetIndex].Tag;
            if (tags == null || tags.Count == 0)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB008",
                    $"route.rule_set[{ruleSetIndex}].tag",
                    "标签不能为空。"));
                continue;
            }

            for (var tagIndex = 0; tagIndex < tags.Count; tagIndex++)
            {
                string tag = tags[tagIndex];
                string path = tags.Count == 1
                    ? $"route.rule_set[{ruleSetIndex}].tag"
                    : $"route.rule_set[{ruleSetIndex}].tag[{tagIndex}]";
                if (string.IsNullOrWhiteSpace(tag))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB008",
                        path,
                        "标签不能为空。"));
                }
                else if (!result.Add(tag))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB009",
                        path,
                        $"标签 '{tag}' 重复。"));
                }
            }
        }

        return result;
    }

    private static HashSet<string> CollectUniqueRequiredTags(
        IEnumerable<string?> tags,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB008",
                    $"{path}[{index}].tag",
                    "标签不能为空。"));
            }
            else if (!result.Add(tag))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB009",
                    $"{path}[{index}].tag",
                    $"标签 '{tag}' 重复。"));
            }

            index++;
        }

        return result;
    }

    private static void ValidateRequiredTags(
        IEnumerable<string?> tags,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        var index = 0;
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB008",
                    $"{path}[{index}].tag",
                    "标签不能为空。"));
            }

            index++;
        }
    }

    private static void ValidateRouteRule(
        RouteRule rule,
        string path,
        HashSet<string> routeTargets,
        HashSet<string> ruleSetTags,
        HashSet<string> inboundTags,
        bool requireAction,
        List<ConfigDiagnostic> diagnostics)
    {
        ValidateReference(
            rule.Outbound,
            routeTargets,
            "SB010",
            $"{path}.outbound",
            "引用了不存在的 outbound 或 endpoint。",
            diagnostics);
        ValidateRuleSets(
            rule.RuleSet,
            ruleSetTags,
            $"{path}.rule_set",
            diagnostics);

        if (rule.PreferredBy != null)
        {
            for (var index = 0; index < rule.PreferredBy.Count; index++)
            {
                ValidateReference(
                    rule.PreferredBy[index],
                    routeTargets,
                    "SB065",
                    $"{path}.preferred_by[{index}]",
                    "引用了不存在的 outbound 或 endpoint。",
                    diagnostics);
            }
        }

        if (rule.Inbound != null)
        {
            for (var index = 0; index < rule.Inbound.Count; index++)
            {
                ValidateReference(
                    rule.Inbound[index],
                    inboundTags,
                    "SB034",
                    $"{path}.inbound[{index}]",
                    "引用了不存在的 inbound。",
                    diagnostics);
            }
        }

        if (requireAction && rule.Action == null)
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB035",
                $"{path}.action",
                "顶层路由规则必须指定 action。"));
        }

        if (rule.Action == RouteRuleAction.Route
            && string.IsNullOrWhiteSpace(rule.Outbound))
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB036",
                $"{path}.outbound",
                "route 动作必须指定 outbound。"));
        }
        else if (rule.Action is not (
                null or RouteRuleAction.Route or RouteRuleAction.Bypass)
            && rule.Outbound != null)
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB037",
                $"{path}.outbound",
                "只有 route 或 bypass 动作可以指定 outbound。"));
        }

        if (rule.Strategy.HasValue
            && rule.Action != RouteRuleAction.Resolve)
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB063",
                $"{path}.strategy",
                "只有 resolve 动作可以指定 DNS strategy。"));
        }

        if (rule.IpVersion is not (null or 4 or 6))
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB073",
                $"{path}.ip_version",
                "ip_version 只能是 4 或 6。"));
        }

        if (rule.NoDrop.HasValue
            && rule.Action != RouteRuleAction.Reject)
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB074",
                $"{path}.no_drop",
                "只有 reject 动作可以指定 no_drop。"));
        }

        if (rule.Rules == null)
        {
            return;
        }

        for (var index = 0; index < rule.Rules.Count; index++)
        {
            ValidateRouteRule(
                rule.Rules[index],
                $"{path}.rules[{index}]",
                routeTargets,
                ruleSetTags,
                inboundTags,
                requireAction: false,
                diagnostics);
        }
    }

    private static void ValidateRuleSets(
        List<string>? references,
        HashSet<string> availableTags,
        string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (references == null)
        {
            return;
        }

        for (var index = 0; index < references.Count; index++)
        {
            ValidateReference(
                references[index],
                availableTags,
                "SB011",
                $"{path}[{index}]",
                "引用了不存在的 rule-set。",
                diagnostics);
        }
    }

    private static void ValidateReference(
        string? reference,
        HashSet<string> availableTags,
        string code,
        string path,
        string message,
        List<ConfigDiagnostic> diagnostics)
    {
        if (reference != null && !availableTags.Contains(reference))
        {
            diagnostics.Add(new ConfigDiagnostic(code, path, message));
        }
    }

    private static void ValidateRequired(
        string? value,
        string code,
        string path,
        string message,
        List<ConfigDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(new ConfigDiagnostic(code, path, message));
        }
    }
}
