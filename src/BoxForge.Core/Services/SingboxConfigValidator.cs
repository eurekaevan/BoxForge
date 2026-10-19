using System.Net;
using System.Net.Sockets;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Configuration;
using BoxForge.Helpers;

namespace BoxForge.Services;

public interface ISingboxConfigValidator
{
    void Validate(SingboxConfig config, AddressFamilyPolicy addressFamily);
}

/// <summary>
/// Validates BoxForge generation constraints and reference integrity.
/// Target syntax and core defaults belong to the automated official <c>sing-box check</c>.
/// Source-field validation remains in the parser and converters.
/// </summary>
public sealed class SingboxConfigValidator : ISingboxConfigValidator
{
    public void Validate(SingboxConfig config, AddressFamilyPolicy addressFamily)
    {
        _ = addressFamily.ToDnsStrategy(); // Reject unknown policies, never silently use DualStack.
        ValidationContext context = CreateValidationContext(config, addressFamily);
        ValidateTopLevelReferences(config.Route, context);
        ValidateHttpClients(config.HttpClients, context);
        ValidateServices(config.Services ?? [], context);
        ValidateOutbounds(config.Outbounds, context);
        ValidateDnsServers(config.Dns.Servers, context);
        ValidateDnsRules(config.Dns, context);
        ValidateRuleSetHttpClients(config.Route.RuleSet, context);
        ValidateCacheFile(config.Experimental?.CacheFile, context.Diagnostics);
        ValidateRouteRules(config.Route.Rules, context);
        ValidateAddressFamilyGuards(config, context);
        if (context.Diagnostics.Count > 0)
            throw new ConfigValidationException(context.Diagnostics);
    }

    private static ValidationContext CreateValidationContext(SingboxConfig config, AddressFamilyPolicy addressFamily)
    {
        var diagnostics = new List<ConfigDiagnostic>();

        var outboundTags = CollectUniqueRequiredTags(
            config.Outbounds.Select(outbound => outbound.Tag),
            "outbounds",
            diagnostics);

        var endpointTags = CollectUniqueRequiredTags(
            config.Endpoints?.Select(endpoint => endpoint.Tag) ?? [],
            "endpoints",
            diagnostics);
        ValidateEndpointTagCollisions(
            config.Endpoints?.Select(endpoint => endpoint.Tag) ?? [],
            outboundTags,
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
        var inboundTags = CollectUniqueRequiredTags(
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
            inboundTags,
            addressFamily);
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
        ValidateReference(
            route.DefaultDomainResolver?.Server,
            context.DnsTags,
            "SB004",
            "route.default_domain_resolver.server",
            "引用了不存在的 DNS server。",
            context.Diagnostics);
        if (context.AddressFamily == AddressFamilyPolicy.Ipv4Only
            && route.DefaultDomainResolver?.Strategy != DnsStrategy.Ipv4Only)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB061",
                "route.default_domain_resolver.strategy",
                "代理节点域名必须使用 ipv4_only 解析策略。"));
        }
        if (route.DefaultDomainResolver?.DisableOptimisticCache != true)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB064",
                "route.default_domain_resolver.disable_optimistic_cache",
                "代理节点域名解析必须禁用 optimistic 过期缓存。"));
        }
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
        ValidateReferences(selector.Outbounds, context.RouteTargets, "SB003", $"outbounds[{index}].outbounds",
            "selector 引用了不存在的目标。", context.Diagnostics);

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
        if (context.AddressFamily == AddressFamilyPolicy.Ipv4Only
            && IPAddress.TryParse(proxy.Server, out IPAddress? serverAddress)
            && serverAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            context.Diagnostics.Add(new ConfigDiagnostic(
                "SB062",
                $"outbounds[{index}].server",
                "代理节点不能使用 IPv6 字面量地址。"));
        }

        ValidateOutboundTls(proxy, index, context.Diagnostics);
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
        if (tls?.Utls is { Enabled: true } utls
            && utls.Fingerprint != "" && !TlsConfigHelper.IsSupportedFingerprint(utls.Fingerprint))
        {
            diagnostics.Add(new ConfigDiagnostic("SB089", $"outbounds[{index}].tls.utls.fingerprint",
                "uTLS 指纹不在 BoxForge 的等价映射支持范围内。"));
        }
        if (tls?.Reality is { Enabled: true } && (!tls.Enabled || tls.Utls?.Enabled != true))
        {
            diagnostics.Add(new ConfigDiagnostic("SB090", $"outbounds[{index}].tls.reality",
                "Reality 必须启用 TLS 和有效的 uTLS。"));
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
            else if (server is HttpsDnsServer httpsServer
                && string.IsNullOrWhiteSpace(httpsServer.Tls?.ServerName))
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB054",
                    $"dns.servers[{index}].tls.server_name",
                    "HTTPS DNS TLS server_name 不能为空。"));
            }
        }
    }

    private static void ValidateDnsRules(
        DnsConfig dns,
        ValidationContext context)
    {
        if (!string.IsNullOrEmpty(dns.Final))
        {
            ValidateReference(dns.Final, context.DnsTags, "SB084", "dns.final",
                "引用了不存在的 DNS server。", context.Diagnostics);
        }

        var evaluatedTags = new HashSet<string>(StringComparer.Ordinal);
        var preferredDomainTags = new HashSet<string>(
            dns.Servers.OfType<TailscaleDnsServer>().Select(server => server.Tag!),
            StringComparer.Ordinal);
        List<DnsRule> rules = dns.Rules;
        for (var index = 0; index < rules.Count; index++)
        {
            DnsRule rule = rules[index];
            string path = $"dns.rules[{index}]";

            ValidateReference(
                rule.Server,
                context.DnsTags,
                "SB007",
                $"{path}.server",
                "引用了不存在的 DNS server。",
                context.Diagnostics);

            ValidateReferences(rule.RuleSet, context.RuleSetTags, "SB011", $"{path}.rule_set",
                "引用了不存在的 rule-set。", context.Diagnostics);
            if (rule.PreferredBy != null)
            {
                for (var preferredIndex = 0; preferredIndex < rule.PreferredBy.Count; preferredIndex++)
                {
                    string reference = rule.PreferredBy[preferredIndex];
                    string referencePath = $"{path}.preferred_by[{preferredIndex}]";
                    ValidateReference(reference, context.DnsTags, "SB087", referencePath,
                        "引用了不存在的 DNS server。", context.Diagnostics);
                    if (context.DnsTags.Contains(reference) && !preferredDomainTags.Contains(reference))
                    {
                        context.Diagnostics.Add(new ConfigDiagnostic("SB088", referencePath,
                            "DNS server 不支持 preferred_by 域名匹配。"));
                    }
                }
            }

            // A tagged response only exists after its evaluate rule, including for
            // an evaluate rule that itself consumes an earlier response.
            ValidateReference(rule.MatchResponse, evaluatedTags, "SB086", $"{path}.match_response",
                "必须引用前置 evaluate 规则定义的响应标签。", context.Diagnostics);

            if (rule.Action == DnsRuleAction.Evaluate
                && string.IsNullOrWhiteSpace(rule.Tag))
            {
                context.Diagnostics.Add(new ConfigDiagnostic(
                    "SB027",
                    $"{path}.tag",
                    "evaluate 响应标签不能为空。"));
            }
            else if (rule.Action == DnsRuleAction.Evaluate && !evaluatedTags.Add(rule.Tag!))
            {
                context.Diagnostics.Add(new ConfigDiagnostic("SB085", $"{path}.tag",
                    $"evaluate 响应标签 '{rule.Tag}' 重复。"));
            }
        }
    }

    private static void ValidateRuleSetHttpClients(
        List<SingboxRuleSet> ruleSets,
        ValidationContext context)
    {
        for (var index = 0; index < ruleSets.Count; index++)
        {
            SingboxRuleSet ruleSet = ruleSets[index];

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

        if (cacheFile.CacheId is not { Length: 64 }
            || cacheFile.CacheId.Any(character => !Uri.IsHexDigit(character)))
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB059",
                "experimental.cache_file.cache_id",
                "cache_id 必须是完整的 64 位 SHA-256 十六进制字符串。"));
        }
    }

    private static void ValidateAddressFamilyGuards(SingboxConfig config, ValidationContext context)
    {
        if (context.AddressFamily != AddressFamilyPolicy.Ipv4Only) return;
        bool forwardingSeen = false;
        bool hasEarlyGuard = false;
        int guardCount = 0;
        bool guardAfterLastScopedResolve = false;
        foreach (RouteRule rule in config.Route.Rules)
        {
            if (IsIpv6RejectGuard(rule, context.InboundTags))
            {
                if (guardCount == 0) hasEarlyGuard = !forwardingSeen;
                guardCount++;
                guardAfterLastScopedResolve = true;
            }

            if (rule.Action == RouteRuleAction.Resolve && rule.RuleSet is { Count: > 0 })
                guardAfterLastScopedResolve = false;
            if (rule.Action is RouteRuleAction.Route or RouteRuleAction.Bypass or RouteRuleAction.Sniff or RouteRuleAction.Resolve)
                forwardingSeen = true;
        }

        if (!hasEarlyGuard)
            context.Diagnostics.Add(new ConfigDiagnostic("SB080", "route.rules",
                "Ipv4Only 必须在 sniff、resolve 或转发之前包含全局 IPv6 reject guard。"));
        if (!guardAfterLastScopedResolve || (hasEarlyGuard && guardCount < 2))
            context.Diagnostics.Add(new ConfigDiagnostic("SB081", "route.rules",
                "Ipv4Only 必须在规则集 resolve 之后保留独立的 IPv6 reject guard。"));

        DnsRule? first = config.Dns.Rules.FirstOrDefault();
        if (first is null || first.Action != DnsRuleAction.Predefined || first.Rcode != DnsResponseCode.NoError
            || first.QueryType is not { Count: 1 } || first.QueryType[0] != "AAAA"
            || first.RuleSet is { Count: > 0 } || first.Domain is { Count: > 0 }
            || first.DomainSuffix is { Count: > 0 } || first.PreferredBy is { Count: > 0 }
            || first.MatchResponse is not null || first.ResponseRcode is not null)
            context.Diagnostics.Add(new ConfigDiagnostic("SB082", "dns.rules[0]",
                "Ipv4Only 必须首先无条件以空 NOERROR 响应拦截 AAAA 查询。"));
    }

    private static bool IsIpv6RejectGuard(RouteRule rule, HashSet<string> inboundTags) =>
        rule.IpVersion == 6 && rule.Action == RouteRuleAction.Reject && rule.Invert != true
        && rule.Type is null && rule.Mode is null && rule.Rules is not { Count: > 0 }
        && rule.RuleSet is not { Count: > 0 } && rule.Protocol is not { Count: > 0 }
        && rule.Port is not { Count: > 0 } && rule.Network is not { Count: > 0 }
        && rule.IpCidr is not { Count: > 0 } && rule.IpIsPrivate != true
        && rule.PreferredBy is not { Count: > 0 }
        && (rule.Inbound is not { Count: > 0 } || inboundTags.IsSubsetOf(rule.Inbound));

    private sealed record ValidationContext(
        List<ConfigDiagnostic> Diagnostics,
        HashSet<string> RouteTargets,
        HashSet<string> ProxyOutboundTags,
        HashSet<string> EndpointTags,
        HashSet<string> DnsTags,
        HashSet<string> HttpClientTags,
        HashSet<string> RuleSetTags,
        HashSet<string> InboundTags,
        AddressFamilyPolicy AddressFamily);

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
                CollectTag(tag, path, result, diagnostics);
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
            CollectTag(tag, $"{path}[{index}].tag", result, diagnostics);
            index++;
        }

        return result;
    }

    private static void CollectTag(
        string? tag,
        string path,
        HashSet<string> tags,
        List<ConfigDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(tag))
            diagnostics.Add(new ConfigDiagnostic("SB008", path, "标签不能为空。"));
        else if (!tags.Add(tag))
            diagnostics.Add(new ConfigDiagnostic("SB009", path, $"标签 '{tag}' 重复。"));
    }

    private static void ValidateEndpointTagCollisions(
        IEnumerable<string?> tags,
        HashSet<string> outboundTags,
        List<ConfigDiagnostic> diagnostics)
    {
        var index = 0;
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag) && outboundTags.Contains(tag))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB009",
                    $"endpoints[{index}].tag",
                    $"标签 '{tag}' 与 outbound tag 重复。"));
            }

            index++;
        }
    }

    private static void ValidateRouteRules(
        List<RouteRule> rules,
        ValidationContext context,
        string path = "route.rules")
    {
        for (var index = 0; index < rules.Count; index++)
        {
            RouteRule rule = rules[index];
            string rulePath = $"{path}[{index}]";
            ValidateReference(rule.Outbound, context.RouteTargets, "SB010", $"{rulePath}.outbound",
                "引用了不存在的 outbound 或 endpoint。", context.Diagnostics);
            ValidateReferences(rule.RuleSet, context.RuleSetTags, "SB011", $"{rulePath}.rule_set",
                "引用了不存在的 rule-set。", context.Diagnostics);

            ValidateReferences(rule.PreferredBy, context.RouteTargets, "SB065", $"{rulePath}.preferred_by",
                "引用了不存在的 outbound 或 endpoint。", context.Diagnostics);
            ValidateReferences(rule.Inbound, context.InboundTags, "SB034", $"{rulePath}.inbound",
                "引用了不存在的 inbound。", context.Diagnostics);
            if (rule.Rules is { } children)
                ValidateRouteRules(children, context, $"{rulePath}.rules");
        }
    }

    private static void ValidateReferences(
        List<string>? references,
        HashSet<string> availableTags,
        string code,
        string path,
        string message,
        List<ConfigDiagnostic> diagnostics)
    {
        if (references is null) return;
        for (var index = 0; index < references.Count; index++)
        {
            ValidateReference(references[index], availableTags, code, $"{path}[{index}]", message, diagnostics);
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
}
