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
        var ruleSetTags = CollectUniqueTags(
            config.Route.RuleSet.Select(ruleSet => ruleSet.Tag),
            "route.rule_set",
            diagnostics);

        ValidateReference(
            config.Route.Final,
            routeTargets,
            "SB001",
            "route.final",
            "不存在对应的 outbound 或 endpoint。",
            diagnostics);

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
            }

            if (outbound is ProxyOutbound proxyOutbound)
            {
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
        }

        for (var index = 0; index < config.Dns.Rules.Count; index++)
        {
            var rule = config.Dns.Rules[index];
            ValidateReference(
                rule.Server,
                dnsTags,
                "SB007",
                $"dns.rules[{index}].server",
                "引用了不存在的 DNS server。",
                diagnostics);
            ValidateRuleSets(
                rule.RuleSet,
                ruleSetTags,
                $"dns.rules[{index}].rule_set",
                diagnostics);
        }

        for (var index = 0; index < config.Route.Rules.Count; index++)
        {
            ValidateRouteRule(
                config.Route.Rules[index],
                $"route.rules[{index}]",
                routeTargets,
                ruleSetTags,
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
                diagnostics);
        }
    }

    private static void ValidateRuleSets(
        IReadOnlyList<string>? references,
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
}
