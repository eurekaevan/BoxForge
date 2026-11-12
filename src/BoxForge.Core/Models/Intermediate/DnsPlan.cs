using BoxForge.Exceptions;
using System.Collections.Immutable;
using System.Net;
using BoxForge.Configuration;
using BoxForge.Helpers;

namespace BoxForge.Models.Intermediate;

internal sealed record DnsPlan(
    AddressFamilyPolicy AddressFamily,
    DnsResolverSelectionMode SelectionMode,
    IReadOnlyList<DnsHttpsResolverPlan> Resolvers,
    bool TailscaleEnabled,
    string FinalResolver,
    string Timeout,
    uint CacheCapacity,
    DomainBlockProfile Block)
{
    internal DnsAddressMode AddressMode { get; init; } = DnsAddressMode.RealIp;
    internal FakeIpPlan? FakeIp { get; init; }
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
        ValidateFakeIp(diagnostics);
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
            if ((pool.Kind == DnsPoolKind.Global) != (index == Pools.Length - 1))
                diagnostics.Add(new("IR011", path, "DNS 全局解析池必须唯一且位于最后。"));
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
        if (Pools.Count(pool => pool?.Kind == DnsPoolKind.Domestic) != 1)
            diagnostics.Add(new("IR011", "plan.dns.pools", "DNS 必须包含唯一的国内解析池。"));
    }

    private void ValidateFakeIp(List<ConfigDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(AddressMode))
        {
            diagnostics.Add(new("IR008", "plan.dns.address_mode", "DNS 地址模式无效。"));
            return;
        }
        if (AddressMode != DnsAddressMode.FakeIp)
        {
            if (FakeIp is not null)
                diagnostics.Add(new("IR008", "plan.dns.fakeip", "RealIp 模式不能包含 FakeIP 计划。"));
            return;
        }
        if (FakeIp is not { } fakeIp)
        {
            diagnostics.Add(new("IR008", "plan.dns.fakeip", "FakeIp 模式必须包含 FakeIP 计划。"));
            return;
        }
        if (!CidrHelper.TryParseCanonical(fakeIp.Inet4Range, out Cidr inet4)
            || inet4.Family != System.Net.Sockets.AddressFamily.InterNetwork)
            diagnostics.Add(new("IR008", "plan.dns.fakeip.inet4_range", "FakeIP IPv4 范围无效。"));
        if (fakeIp.Inet6Range is { } inet6Range)
        {
            if (!CidrHelper.TryParseCanonical(inet6Range, out Cidr inet6)
                || inet6.Family != System.Net.Sockets.AddressFamily.InterNetworkV6)
                diagnostics.Add(new("IR008", "plan.dns.fakeip.inet6_range", "FakeIP IPv6 范围无效。"));
        }
        else if (AddressFamily == AddressFamilyPolicy.DualStack)
            diagnostics.Add(new("IR008", "plan.dns.fakeip.inet6_range", "DualStack 的 FakeIP 必须提供 IPv6 范围。"));
        ValidateDomainList(fakeIp.RealIpDomains, "plan.dns.fakeip.realip_domains", diagnostics);
        ValidateDomainList(fakeIp.RealIpDomainSuffixes, "plan.dns.fakeip.realip_domain_suffixes", diagnostics);
    }

    private static void ValidateDomainList(ImmutableArray<string> domains, string path, List<ConfigDiagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < domains.Length; index++)
        {
            string value = domains[index];
            if (string.IsNullOrEmpty(value) || value.Length > 253 || IPAddress.TryParse(value, out _)
                || value.Split('.').Any(label => label.Length is < 1 or > 63
                    || label[0] == '-' || label[^1] == '-'
                    || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                diagnostics.Add(new("IR008", $"{path}[{index}]", "FakeIP 例外必须为 ASCII 域名。"));
            else if (!seen.Add(value))
                diagnostics.Add(new("IR011", $"{path}[{index}]", "FakeIP 例外域名不能重复。"));
        }
    }
}

internal sealed record FakeIpPlan(
    string Inet4Range,
    string? Inet6Range,
    ImmutableArray<string> RealIpDomains,
    ImmutableArray<string> RealIpDomainSuffixes);

internal sealed record DnsHttpsResolverPlan(string Name, string Address, string ServerName, string? Detour);

internal sealed record DnsResolverPoolPlan(
    DnsPoolKind Kind,
    ImmutableArray<string> RuleSets,
    string PrimaryServer,
    string SecondaryServer,
    string? ServiceId = null);

internal enum DnsPoolKind { Service, Domestic, Global }
