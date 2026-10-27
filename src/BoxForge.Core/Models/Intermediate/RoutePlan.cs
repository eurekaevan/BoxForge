using BoxForge.Exceptions;
using BoxForge.Configuration;
using BoxForge.Helpers;
using System.Collections.Immutable;
using System.Net;

namespace BoxForge.Models.Intermediate;

internal sealed record RoutePlan(
    AddressFamilyPolicy AddressFamily,
    bool TailscaleEnabled,
    bool UseDirectForwarding,
    string FinalOutbound,
    string DefaultHttpClient,
    DomainResolverPlan DefaultDomainResolver,
    IReadOnlyList<RuleSetPlan> RuleSets,
    string BootstrapAddress,
    string SniffTimeout,
    ImmutableArray<int> SniffExcludedPorts,
    ImmutableArray<ServiceRoutePlan> Services)
{
    internal void Validate()
    {
        var diagnostics = new List<ConfigDiagnostic>();
        CollectDiagnostics(diagnostics);
        ConfigValidationException.ThrowIfAny(diagnostics);
    }

    internal void CollectDiagnostics(List<ConfigDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(AddressFamily))
            diagnostics.Add(new("IR008", "plan.route.address_family", "路由地址族无效。"));
        if (!IPAddress.TryParse(BootstrapAddress, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.ToString() != BootstrapAddress)
            diagnostics.Add(new("IR008", "plan.route.bootstrap_address", "直连 bootstrap 必须使用 IPv4 地址。"));
        if (SniffTimeout is null || !GoDurationHelper.IsPositiveDuration(SniffTimeout))
            diagnostics.Add(new("IR008", "plan.route.sniff_timeout", "Sniff 超时必须是有效的正 Go duration。"));
        if (SniffExcludedPorts.IsDefault)
            diagnostics.Add(new("IR008", "plan.route.sniff_excluded_ports", "Sniff 排除端口必须初始化。"));
        else if (SniffExcludedPorts.Any(port => port is < 1 or > 65535)
            || SniffExcludedPorts.Distinct().Count() != SniffExcludedPorts.Length)
            diagnostics.Add(new("IR008", "plan.route.sniff_excluded_ports", "Sniff 排除端口必须有效且唯一。"));

        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (int sourceIndex = 0; sourceIndex < RuleSets.Count; sourceIndex++)
        {
            RuleSetPlan source = RuleSets[sourceIndex];
            string path = $"plan.route.rule_sets[{sourceIndex}].tags";
            if (source.Tags.IsDefaultOrEmpty)
                diagnostics.Add(new("IR009", path, "路由规则集必须声明非空标签。"));
            if (!source.Tags.IsDefault)
                for (int index = 0; index < source.Tags.Length; index++)
                    if (string.IsNullOrWhiteSpace(source.Tags[index]) || !tags.Add(source.Tags[index]))
                        diagnostics.Add(new("IR009", $"{path}[{index}]", "路由规则集标签必须非空且唯一。"));
        }
        if (!tags.IsSupersetOf([RuleSetTags.Ads, RuleSetTags.Cn, RuleSetTags.CnIp]))
            diagnostics.Add(new("IR009", "plan.route.rule_sets", "路由计划缺少广告或国内规则集。"));
        if (Services.IsDefault)
        {
            diagnostics.Add(new("IR008", "plan.route.services", "路由服务数组必须初始化。"));
            return;
        }
        var outbounds = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool ordinarySeen = false;
        for (int index = 0; index < Services.Length; index++)
        {
            string path = $"plan.route.services[{index}]";
            if (Services[index] is not { } service)
            {
                diagnostics.Add(new("IR008", path, "路由服务不能为 null。"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(service.ServiceId) || !ids.Add(service.ServiceId))
                diagnostics.Add(new("IR009", path + ".service_id", "路由服务标识必须非空且唯一。"));
            if (string.IsNullOrWhiteSpace(service.Outbound) || !outbounds.Add(service.Outbound))
                diagnostics.Add(new("IR009", path + ".outbound", "路由服务出口必须非空且唯一。"));
            if (service.PrecedesDomesticRoutes && ordinarySeen)
                diagnostics.Add(new("IR010", path + ".priority", "优先服务必须位于普通服务之前。"));
            ordinarySeen |= !service.PrecedesDomesticRoutes;
            if (service.RuleSets.IsDefaultOrEmpty)
                diagnostics.Add(new("IR008", path + ".rule_sets", "路由服务必须限定规则集。"));
            else
                for (int scope = 0; scope < service.RuleSets.Length; scope++)
                    if (!tags.Contains(service.RuleSets[scope]))
                        diagnostics.Add(new("IR005", $"{path}.rule_sets[{scope}]", "路由服务引用了未声明的规则集。"));
        }
    }
}

internal sealed record RuleSetPlan(ImmutableArray<string> Tags, string Url, string UpdateInterval);

internal sealed record ServiceRoutePlan(
    string ServiceId, ImmutableArray<string> RuleSets, string Outbound, bool PrecedesDomesticRoutes);
