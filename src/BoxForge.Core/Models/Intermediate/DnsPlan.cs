using BoxForge.Exceptions;
using System.Collections.Immutable;
using BoxForge.Configuration;

namespace BoxForge.Models.Intermediate;

internal sealed record DnsPlan(
    AddressFamilyPolicy AddressFamily,
    DnsResolverSelectionMode SelectionMode,
    IReadOnlyList<DnsHttpsResolverPlan> Resolvers,
    bool TailscaleEnabled,
    string FinalResolver,
    string Timeout,
    uint CacheCapacity,
    string OptimisticTimeout)
{
    internal required ImmutableArray<DnsResolverPoolPlan> Pools { get; init; }

    internal void Validate()
    {
        var diagnostics = new List<ConfigDiagnostic>();
        CollectDiagnostics(diagnostics);
        ConfigValidationException.ThrowIfAny(diagnostics);
    }

    internal void CollectDiagnostics(List<ConfigDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(AddressFamily))
            diagnostics.Add(new("IR008", "plan.dns.address_family", "DNS 地址族无效。"));
        if (!Enum.IsDefined(SelectionMode))
            diagnostics.Add(new("IR008", "plan.dns.selection_mode", "DNS 选择模式无效。"));
        var resolvers = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Resolvers.Count; index++)
        {
            DnsHttpsResolverPlan resolver = Resolvers[index];
            string path = $"plan.dns.resolvers[{index}]";
            if (string.IsNullOrWhiteSpace(resolver.Name) || !resolvers.Add(resolver.Name))
                diagnostics.Add(new("IR009", path + ".name", "DNS 解析器名称必须非空且唯一。"));
            if (string.IsNullOrWhiteSpace(resolver.Address))
                diagnostics.Add(new("IR008", path + ".address", "DNS 解析器地址不能为空。"));
            if (string.IsNullOrWhiteSpace(resolver.ServerName))
                diagnostics.Add(new("IR008", path + ".server_name", "DNS 解析器 TLS 名称不能为空。"));
        }
        if (!resolvers.Contains(FinalResolver))
            diagnostics.Add(new("IR006", "plan.dns.final_resolver", "DNS 最终解析器必须已声明。"));
        if (Pools.IsDefaultOrEmpty)
        {
            diagnostics.Add(new("IR008", "plan.dns.pools", "DNS 计划必须包含解析池。"));
            return;
        }
        var services = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Pools.Length; index++)
        {
            string path = $"plan.dns.pools[{index}]";
            if (Pools[index] is not { } pool)
            {
                diagnostics.Add(new("IR008", path, "DNS 解析池不能为 null。"));
                continue;
            }
            if (!Enum.IsDefined(pool.Kind))
                diagnostics.Add(new("IR008", path + ".kind", "DNS 解析池类型无效。"));
            DnsPoolKind expected = index < Pools.Length - 2 ? DnsPoolKind.Service
                : index == Pools.Length - 2 ? DnsPoolKind.Domestic : DnsPoolKind.Global;
            if (pool.Kind != expected || Pools.Length < 2)
                diagnostics.Add(new("IR011", path, "DNS 解析池必须依次包含各服务、国内和全局池。"));
            if (pool.Kind == DnsPoolKind.Service)
            {
                if (string.IsNullOrWhiteSpace(pool.ServiceId) || !services.Add(pool.ServiceId))
                    diagnostics.Add(new("IR009", path + ".service_id", "DNS 服务标识必须非空且唯一。"));
                if (pool.RuleSets.IsDefaultOrEmpty)
                    diagnostics.Add(new("IR011", path, "DNS 服务解析池必须限定规则集。"));
            }
            else if (pool.ServiceId is not null
                || (pool.Kind == DnsPoolKind.Global ? !pool.RuleSets.IsDefault
                    : pool.RuleSets.IsDefault || !pool.RuleSets.SequenceEqual([RuleSetTags.Cn])))
                diagnostics.Add(new("IR011", path, "国内和全局解析池的范围或服务绑定无效。"));
            if (!resolvers.Contains(pool.PrimaryServer))
                diagnostics.Add(new("IR006", path + ".primary_server", "DNS 解析池引用了未声明的主解析器。"));
            if (!resolvers.Contains(pool.SecondaryServer))
                diagnostics.Add(new("IR006", path + ".secondary_server", "DNS 解析池引用了未声明的备解析器。"));
            if (pool.PrimaryServer == pool.SecondaryServer)
                diagnostics.Add(new("IR011", path, "DNS 解析池主备解析器必须不同。"));
        }
    }
}

internal sealed record DnsHttpsResolverPlan(string Name, string Address, string ServerName, string? Detour);

internal sealed record DnsResolverPoolPlan(
    DnsPoolKind Kind,
    ImmutableArray<string> RuleSets,
    string PrimaryServer,
    string SecondaryServer,
    string? ServiceId = null);

internal enum DnsPoolKind { Service, Domestic, Global }
