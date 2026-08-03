using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Services;

public interface ISingboxConfigValidator
{
    void Validate(SingboxConfig config);
}

public sealed class SingboxConfigValidator : ISingboxConfigValidator
{
    public void Validate(SingboxConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var outboundTags = CollectUniqueTags(
            config.Outbounds.Select(outbound => outbound.Tag),
            "outbounds",
            diagnostics);
        var endpointTags = CollectUniqueTags(
            config.Endpoints?.Select(endpoint => endpoint.Tag) ?? [],
            "endpoints",
            diagnostics);
        var routeTargets = new HashSet<string>(outboundTags, StringComparer.Ordinal);
        foreach (var endpointTag in endpointTags)
        {
            if (routeTargets.Contains(endpointTag))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB012",
                    "endpoints",
                    $"endpoint 标签 '{endpointTag}' 与 outbound 标签重复。"));
            }
        }
        routeTargets.UnionWith(endpointTags);

        var dnsTags = CollectUniqueTags(
            config.Dns.Servers.Select(server => server.Tag),
            "dns.servers",
            diagnostics);
        var httpClientTags = CollectUniqueTags(
            config.HttpClients.Select(client => client.Tag),
            "http_clients",
            diagnostics);
        var ruleSetTags = CollectUniqueTags(
            config.Route.RuleSet.Select(ruleSet => ruleSet.Tag),
            "route.rule_set",
            diagnostics);
        var inboundTags = CollectUniqueTags(
            config.Inbounds.Select(inbound => inbound.Tag),
            "inbounds",
            diagnostics);
        _ = CollectUniqueTags(
            config.Services?.Select(service => service.Tag) ?? [],
            "services",
            diagnostics);

        ValidateReference(
            config.Route.Final,
            routeTargets,
            "SB001",
            "route.final",
            "不存在对应的 outbound 或 endpoint。",
            diagnostics);
        ValidateReference(
            config.Route.DefaultHttpClient,
            httpClientTags,
            "SB014",
            "route.default_http_client",
            "引用了不存在的 HTTP client。",
            diagnostics);

        for (var index = 0; index < (config.Endpoints?.Count ?? 0); index++)
        {
            if (config.Endpoints![index] is not TailscaleEndpoint tailscaleEndpoint)
            {
                continue;
            }

            ValidateReference(
                tailscaleEndpoint.DomainResolver,
                dnsTags,
                "SB016",
                $"endpoints[{index}].domain_resolver",
                "引用了不存在的 DNS server。",
                diagnostics);
        }

        for (var index = 0; index < config.HttpClients.Count; index++)
        {
            ValidateReference(
                config.HttpClients[index].Detour,
                routeTargets,
                "SB015",
                $"http_clients[{index}].detour",
                "引用了不存在的 outbound 或 endpoint。",
                diagnostics);
        }

        for (var index = 0; index < config.Outbounds.Count; index++)
        {
            var outbound = config.Outbounds[index];
            if (outbound is SelectorOutbound selector)
            {
                if (selector.Outbounds.Count == 0)
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB002",
                        $"outbounds[{index}].outbounds",
                        "selector 不能为空。"));
                }

                for (var childIndex = 0; childIndex < selector.Outbounds.Count; childIndex++)
                {
                    ValidateReference(
                        selector.Outbounds[childIndex],
                        routeTargets,
                        "SB003",
                        $"outbounds[{index}].outbounds[{childIndex}]",
                        "selector 引用了不存在的目标。",
                        diagnostics);
                }

                if (selector.Outbounds.Count != selector.Outbounds.Distinct(
                        StringComparer.Ordinal).Count())
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB017",
                        $"outbounds[{index}].outbounds",
                        "selector 不能包含重复目标。"));
                }

                if (selector.Default != null
                    && !selector.Outbounds.Contains(
                        selector.Default,
                        StringComparer.Ordinal))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB018",
                        $"outbounds[{index}].default",
                        "selector 默认目标不在 outbounds 中。"));
                }
            }

            if (outbound is ProxyOutbound proxyOutbound)
            {
                if (string.IsNullOrWhiteSpace(proxyOutbound.Server))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB019",
                        $"outbounds[{index}].server",
                        "代理服务器地址不能为空。"));
                }

                bool hasValidSinglePort = proxyOutbound.ServerPort is > 0 and <= 65535;
                bool hasPortSet = proxyOutbound is Hysteria2Outbound
                {
                    ServerPorts.Count: > 0
                };
                if (!hasValidSinglePort && !hasPortSet)
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB020",
                        $"outbounds[{index}].server_port",
                        "代理节点必须配置有效端口。"));
                }

                ValidateReference(
                    proxyOutbound.DomainResolver,
                    dnsTags,
                    "SB004",
                    $"outbounds[{index}].domain_resolver",
                    "引用了不存在的 DNS server。",
                    diagnostics);

                var tls = proxyOutbound switch
                {
                    VlessOutbound vless => vless.Tls,
                    TrojanOutbound trojan => trojan.Tls,
                    Hysteria2Outbound hysteria2 => hysteria2.Tls,
                    AnyTlsOutbound anyTls => anyTls.Tls,
                    _ => null
                };
                if (tls?.Reality is { } reality
                    && string.IsNullOrWhiteSpace(reality.PublicKey))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB013",
                        $"outbounds[{index}].tls.reality.public_key",
                        "Reality 公钥不能为空。"));
                }

                if (tls is { Enabled: true }
                    && string.IsNullOrWhiteSpace(tls.ServerName))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB044",
                        $"outbounds[{index}].tls.server_name",
                        "TLS server_name 不能为空。"));
                }

                switch (proxyOutbound)
                {
                    case VlessOutbound vless:
                        ValidateRequired(vless.Uuid, "SB045", $"outbounds[{index}].uuid", "VLESS UUID 不能为空。", diagnostics);
                        break;
                    case TrojanOutbound trojan:
                        ValidateRequired(trojan.Password, "SB046", $"outbounds[{index}].password", "Trojan 密码不能为空。", diagnostics);
                        break;
                    case Hysteria2Outbound hysteria2:
                        ValidateRequired(hysteria2.Password, "SB047", $"outbounds[{index}].password", "Hysteria2 密码不能为空。", diagnostics);
                        break;
                    case ShadowsocksOutbound shadowsocks:
                        ValidateRequired(shadowsocks.Method, "SB048", $"outbounds[{index}].method", "Shadowsocks 加密方法不能为空。", diagnostics);
                        ValidateRequired(shadowsocks.Password, "SB049", $"outbounds[{index}].password", "Shadowsocks 密码不能为空。", diagnostics);
                        break;
                    case AnyTlsOutbound anyTls:
                        ValidateRequired(anyTls.Password, "SB050", $"outbounds[{index}].password", "AnyTLS 密码不能为空。", diagnostics);
                        break;
                }
            }
        }

        for (var index = 0; index < config.Inbounds.Count; index++)
        {
            var inbound = config.Inbounds[index];
            if (string.IsNullOrWhiteSpace(inbound.Type))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB021",
                    $"inbounds[{index}].type",
                    "inbound 类型不能为空。"));
            }

            if (inbound.ListenPort is int listenPort
                && listenPort is <= 0 or > 65535)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB022",
                    $"inbounds[{index}].listen_port",
                    "inbound 监听端口必须在 1-65535 之间。"));
            }
        }

        for (var index = 0; index < (config.Services?.Count ?? 0); index++)
        {
            if (config.Services![index] is SingboxApiService apiService)
            {
                if (string.IsNullOrWhiteSpace(apiService.Secret))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB023",
                        $"services[{index}].secret",
                        "API 服务密钥不能为空。"));
                }

                if (apiService.ListenPort is <= 0 or > 65535)
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB024",
                        $"services[{index}].listen_port",
                        "API 服务端口必须在 1-65535 之间。"));
                }

                ValidateRequired(
                    apiService.Dashboard.Path,
                    "SB051",
                    $"services[{index}].dashboard.path",
                    "API dashboard 路径不能为空。",
                    diagnostics);
                ValidateReference(
                    apiService.Dashboard.HttpClient,
                    httpClientTags,
                    "SB052",
                    $"services[{index}].dashboard.http_client",
                    "引用了不存在的 HTTP client。",
                    diagnostics);
            }
        }

        for (var index = 0; index < config.Dns.Servers.Count; index++)
        {
            var server = config.Dns.Servers[index];
            ValidateReference(
                server.Detour,
                routeTargets,
                "SB005",
                $"dns.servers[{index}].detour",
                "引用了不存在的 outbound 或 endpoint。",
                diagnostics);

            if (server is TailscaleDnsServer tailscaleServer)
            {
                ValidateReference(
                    tailscaleServer.Endpoint,
                    endpointTags,
                    "SB006",
                    $"dns.servers[{index}].endpoint",
                    "引用了不存在的 Tailscale endpoint。",
                    diagnostics);
            }
            else if (server is HttpsDnsServer httpsServer)
            {
                ValidateRequired(
                    httpsServer.Server,
                    "SB053",
                    $"dns.servers[{index}].server",
                    "HTTPS DNS server 不能为空。",
                    diagnostics);
                ValidateRequired(
                    httpsServer.Tls?.ServerName,
                    "SB054",
                    $"dns.servers[{index}].tls.server_name",
                    "HTTPS DNS TLS server_name 不能为空。",
                    diagnostics);
            }
        }

        var responseTags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < config.Dns.Rules.Count; index++)
        {
            var rule = config.Dns.Rules[index];
            string path = $"dns.rules[{index}]";
            if (rule.Action == null)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB025",
                    $"{path}.action",
                    "DNS 规则动作不能为空。"));
            }

            ValidateReference(
                rule.Server,
                dnsTags,
                "SB007",
                $"{path}.server",
                "引用了不存在的 DNS server。",
                diagnostics);
            ValidateRuleSets(
                rule.RuleSet,
                ruleSetTags,
                $"{path}.rule_set",
                diagnostics);

            if (rule.Action is DnsRuleAction.Route or DnsRuleAction.Evaluate
                && string.IsNullOrWhiteSpace(rule.Server))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB026",
                    $"{path}.server",
                    "route/evaluate 动作必须指定 DNS server。"));
            }

            if (rule.Action == DnsRuleAction.Evaluate)
            {
                if (string.IsNullOrWhiteSpace(rule.Tag)
                    || !responseTags.Add(rule.Tag))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB027",
                        $"{path}.tag",
                        "evaluate 响应标签不能为空或重复。"));
                }
            }
            else if (rule.Tag != null)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB028",
                    $"{path}.tag",
                    "只有 evaluate 动作可以设置响应标签。"));
            }

            if (rule.Action is not (DnsRuleAction.Route or DnsRuleAction.Evaluate)
                && rule.Server != null)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB041",
                    $"{path}.server",
                    "只有 route/evaluate 动作可以指定 DNS server。"));
            }

            if (rule.Action == DnsRuleAction.Predefined
                && !rule.Rcode.HasValue)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB042",
                    $"{path}.rcode",
                    "predefined 动作必须指定 rcode。"));
            }
            else if (rule.Action != DnsRuleAction.Predefined
                && rule.Rcode.HasValue)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB043",
                    $"{path}.rcode",
                    "只有 predefined 动作可以指定 rcode。"));
            }

            if (rule.MatchResponse != null
                && !responseTags.Contains(rule.MatchResponse))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB029",
                    $"{path}.match_response",
                    "引用了不存在或尚未 evaluate 的响应标签。"));
            }

            bool usesResponseMatch = rule.IpAcceptAny == true
                || rule.ResponseRcode.HasValue
                || rule.Race == true
                || rule.Action == DnsRuleAction.Respond;
            if (usesResponseMatch
                && string.IsNullOrWhiteSpace(rule.MatchResponse))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB030",
                    $"{path}.match_response",
                    "响应匹配规则必须指定 match_response。"));
            }

            if (rule.Race == true)
            {
                if (rule.Action != DnsRuleAction.Respond)
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB031",
                        $"{path}.race",
                        "race 不支持当前 DNS 动作。"));
                }

            }

            if (rule.Speculative == true)
            {
                if (rule.Action is not (DnsRuleAction.Route or DnsRuleAction.Evaluate))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB032",
                        $"{path}.speculative",
                        "speculative 只支持 route/evaluate 动作。"));
                }

                if (index == 0 || config.Dns.Rules[index - 1].Race != true)
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        "SB033",
                        $"{path}.speculative",
                        "speculative 前必须存在 race 规则。"));
                }
            }
        }

        for (var index = 0; index < config.Route.RuleSet.Count; index++)
        {
            SingboxRuleSet ruleSet = config.Route.RuleSet[index];
            if (!ruleSet.Type.HasValue)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB055",
                    $"route.rule_set[{index}].type",
                    "rule-set type 不能为空。"));
            }

            if (!ruleSet.Format.HasValue)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB056",
                    $"route.rule_set[{index}].format",
                    "rule-set format 不能为空。"));
            }

            ValidateRequired(ruleSet.Url, "SB057", $"route.rule_set[{index}].url", "远程 rule-set URL 不能为空。", diagnostics);
        }

        if (config.Experimental?.CacheFile is { Enabled: true } cacheFile)
        {
            ValidateRequired(cacheFile.Path, "SB058", "experimental.cache_file.path", "缓存文件路径不能为空。", diagnostics);
            if (cacheFile.CacheId is not { Length: 64 }
                || cacheFile.CacheId.Any(character => !Uri.IsHexDigit(character)))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB059",
                    "experimental.cache_file.cache_id",
                    "cache_id 必须是完整的 64 位 SHA-256 十六进制字符串。"));
            }
        }

        for (var index = 0; index < config.Route.Rules.Count; index++)
        {
            ValidateRouteRule(
                config.Route.Rules[index],
                $"route.rules[{index}]",
                routeTargets,
                ruleSetTags,
                inboundTags,
                requireAction: true,
                diagnostics);
        }

        if (diagnostics.Count > 0)
        {
            throw new ConfigValidationException(diagnostics);
        }
    }

    private static HashSet<string> CollectUniqueTags(
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
        else if (rule.Action is not (null or RouteRuleAction.Route)
            && rule.Outbound != null)
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB037",
                $"{path}.outbound",
                "只有 route 动作可以指定 outbound。"));
        }

        bool isLogical = rule.Type == RouteRuleType.Logical;
        if (isLogical)
        {
            if (rule.Mode is not (RouteLogicalMode.And or RouteLogicalMode.Or))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB038",
                    $"{path}.mode",
                    "logical 规则的 mode 必须是 and 或 or。"));
            }

            if (rule.Rules is not { Count: > 0 })
            {
                diagnostics.Add(new ConfigDiagnostic(
                    "SB039",
                    $"{path}.rules",
                    "logical 规则必须包含子规则。"));
            }
        }
        else if (rule.Mode != null || rule.Rules != null)
        {
            diagnostics.Add(new ConfigDiagnostic(
                "SB040",
                $"{path}.type",
                "mode/rules 只能用于 logical 路由规则。"));
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
