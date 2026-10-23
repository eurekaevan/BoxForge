using BoxForge.Exceptions;
using BoxForge.Models;
using System.Collections.Immutable;
using BoxForge.Configuration;

namespace BoxForge.Models.Intermediate;

internal sealed record DnsPlan(
    AddressFamilyPolicy AddressFamily,
    DnsResolverSelectionMode SelectionMode,
    IReadOnlyList<DnsHttpsResolverPlan> Resolvers,
    bool TailscaleEnabled,
    IReadOnlyList<string> BlockedRuleSets,
    IReadOnlyList<DnsResolverPoolPlan> Pools,
    string FinalResolver,
    string Timeout,
    uint CacheCapacity,
    string OptimisticTimeout)
{
    internal const string SequentialPrimaryTimeout = "700ms";

    // Rules alone drive emission. Pools and blocking metadata describe the expected policy.
    internal required ImmutableArray<DnsRulePlan> Rules { get; init; }

    internal void Validate()
    {
        var diagnostics = new List<ConfigDiagnostic>();
        CollectDiagnostics(diagnostics);
        ConfigValidationException.ThrowIfAny(diagnostics);
    }

    internal void CollectDiagnostics(List<ConfigDiagnostic> diagnostics)
    {
        int initialDiagnostics = diagnostics.Count;
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
        if (BlockedRuleSets.Count == 0)
            diagnostics.Add(new("IR008", "plan.dns.blocked_rule_sets", "DNS 屏蔽必须限定规则集。"));
        if (Pools.Count == 0 || Pools[^1].RuleSets is not null)
            diagnostics.Add(new("IR010", "plan.dns.pools", "DNS 计划必须以全局解析池结束。"));
        if (Rules.IsDefaultOrEmpty)
            diagnostics.Add(new("IR008", "plan.dns.rules", "DNS 计划必须包含显式规则。"));
        if (TailscaleEnabled)
            resolvers.Add(SingboxTags.TailscaleDns);
        var responses = new HashSet<string>(StringComparer.Ordinal);
        if (!Rules.IsDefault)
            for (int index = 0; index < Rules.Length; index++)
                ValidateRule(Rules[index], $"plan.dns.rules[{index}]");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Pools.Count; index++)
        {
            DnsResolverPoolPlan pool = Pools[index];
            string path = $"plan.dns.pools[{index}]";
            if (string.IsNullOrWhiteSpace(pool.Name) || !names.Add(pool.Name))
                diagnostics.Add(new("IR009", path + ".name", "DNS 解析池名称必须非空且唯一。"));
            if (!resolvers.Contains(pool.PrimaryResolver))
                diagnostics.Add(new("IR006", path + ".primary_resolver", "DNS 解析池引用了未声明的解析器。"));
            if (!resolvers.Contains(pool.SecondaryResolver))
                diagnostics.Add(new("IR006", path + ".secondary_resolver", "DNS 解析池引用了未声明的解析器。"));
            if (pool.RuleSets is { Count: 0 } || (index < Pools.Count - 1 && pool.RuleSets is null))
                diagnostics.Add(new("IR010", path + ".rule_sets", "限定解析池必须具有规则集并位于全局解析池之前。"));
        }

        // Do not cascade policy errors from malformed declarations or rule trees.
        if (diagnostics.Count == initialDiagnostics)
            ValidatePolicy(diagnostics);

        void ValidateRule(DnsRulePlan rule, string path)
        {
            if (rule.Mode is { } mode && !Enum.IsDefined(mode))
                diagnostics.Add(new("IR008", path + ".mode", "DNS 逻辑匹配模式无效。"));
            if (rule.Action is { } action && !Enum.IsDefined(action))
                diagnostics.Add(new("IR008", path + ".action", "DNS 规则动作无效。"));
            if (rule.Rcode is { } rcode && !Enum.IsDefined(rcode))
                diagnostics.Add(new("IR008", path + ".rcode", "DNS 结果码无效。"));
            if (rule.ResponseRcode is { } responseRcode && !Enum.IsDefined(responseRcode))
                diagnostics.Add(new("IR008", path + ".response_rcode", "DNS 响应结果码无效。"));
            if (rule.Mode is not null && rule.Rules.IsDefaultOrEmpty)
                diagnostics.Add(new("IR008", path + ".rules", "DNS 逻辑规则必须包含子条件。"));
            if (rule.Mode is null && !rule.Rules.IsDefault)
                diagnostics.Add(new("IR008", path + ".mode", "DNS 子条件必须指定逻辑匹配模式。"));
            if (!rule.Rules.IsDefault)
                for (int index = 0; index < rule.Rules.Length; index++)
                    ValidateRule(rule.Rules[index], $"{path}.rules[{index}]");
            if (rule.Server is { } server && !resolvers.Contains(server))
                diagnostics.Add(new("IR006", path + ".server", "DNS 规则引用了未声明的解析器。"));
            if (rule.Action is DnsOperation.Route or DnsOperation.Evaluate && rule.Server is null)
                diagnostics.Add(new("IR008", path + ".server", "DNS 路由和求值必须指定解析器。"));
            if (rule.MatchResponse is { } response && !responses.Contains(response))
                diagnostics.Add(new("IR010", path + ".match_response", "DNS 响应必须先求值再引用。"));
            if (rule.Action == DnsOperation.Evaluate
                && (string.IsNullOrWhiteSpace(rule.Tag) || !responses.Add(rule.Tag)))
                diagnostics.Add(new("IR009", path + ".tag", "DNS 求值必须具有非空且唯一的响应标签。"));
            if (rule.Action == DnsOperation.Respond && rule.MatchResponse is null)
                diagnostics.Add(new("IR008", path + ".match_response", "DNS 响应规则必须指定已求值的响应。"));
        }
    }

    private void ValidatePolicy(List<ConfigDiagnostic> diagnostics)
    {
        int prefix = 0;
        if (AddressFamily != AddressFamilyPolicy.DualStack)
        {
            var guard = new DnsRulePlan { QueryType = ["AAAA"], Action = DnsOperation.Predefined, Rcode = DnsResultCode.NoError };
            if (AddressFamily == AddressFamilyPolicy.Auto)
                guard = guard with
                {
                    QueryType = default,
                    Mode = LogicalMatchMode.And,
                    Rules = [new() { QueryType = ["AAAA"] }, new() { DefaultInterfaceAddress = [AddressFamilyPolicies.GlobalIpv6Prefix], Invert = true }]
                };
            RequireAt(prefix++, guard);
        }
        if (TailscaleEnabled)
            RequireAt(prefix++, new()
            {
                PreferredBy = [SingboxTags.TailscaleDns],
                Action = DnsOperation.Route,
                Server = SingboxTags.TailscaleDns,
                DisableOptimisticCache = true
            });
        RequireAt(prefix++, new() { RuleSet = [.. BlockedRuleSets], Action = DnsOperation.Predefined, Rcode = DnsResultCode.NameError });
        int evaluationsPerPool = SelectionMode == DnsResolverSelectionMode.ParallelFastest ? 4 : 2;
        if (Rules.Count(rule => rule.Action == DnsOperation.Evaluate) != Pools.Count * evaluationsPerPool)
            diagnostics.Add(new("IR011", "plan.dns.pools", "DNS 解析池与实际求值规则的数量不一致。"));
        int previous = prefix - 1;
        for (int index = 0; index < Pools.Count; index++)
        {
            DnsResolverPoolPlan pool = Pools[index];
            bool valid = true;
            foreach (bool addressQuery in SelectionMode == DnsResolverSelectionMode.ParallelFastest ? new[] { true, false } : new[] { false })
                foreach (bool primary in new[] { true, false })
                {
                    string tag = $"response-{pool.Name}-{(primary ? "primary" : "secondary")}";
                    if (addressQuery)
                        tag = DnsResponseTags.ForAddressQuery(tag);
                    var expected = new DnsRulePlan
                    {
                        RuleSet = pool.RuleSets is null ? default : [.. pool.RuleSets],
                        QueryType = addressQuery ? ["A"] : default,
                        DisableOptimisticCache = addressQuery ? true : null,
                        Action = DnsOperation.Evaluate,
                        Server = primary ? pool.PrimaryResolver : pool.SecondaryResolver,
                        Tag = tag,
                        Timeout = SelectionMode == DnsResolverSelectionMode.SequentialFallback && primary ? SequentialPrimaryTimeout : null
                    };
                    int position = -1;
                    for (int ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
                        if (Rules[ruleIndex].Tag == tag)
                            position = ruleIndex;
                    valid &= position > previous && position >= 0 && Rules[position].HasSamePolicy(expected);
                    previous = Math.Max(previous, position);
                }
            if (!valid)
                diagnostics.Add(new("IR011", $"plan.dns.pools[{index}]", "DNS 解析池与实际求值规则的绑定、范围或顺序不一致。"));
        }

        void RequireAt(int index, DnsRulePlan expected)
        {
            if (index >= Rules.Length || !Rules[index].HasSamePolicy(expected))
                diagnostics.Add(new("IR011", $"plan.dns.rules[{index}]", "DNS 必要守卫、控制面或屏蔽规则缺失或不符合策略。"));
        }
    }
}

internal sealed record DnsHttpsResolverPlan(string Name, string Address, string ServerName, string? Detour);

internal sealed record DnsResolverPoolPlan(
    string Name, IReadOnlyList<string>? RuleSets, string PrimaryResolver, string SecondaryResolver);

internal sealed record DnsRulePlan
{
    internal LogicalMatchMode? Mode { get; init; }
    internal ImmutableArray<DnsRulePlan> Rules { get; init; }
    internal bool? Invert { get; init; }
    internal ImmutableArray<string> DefaultInterfaceAddress { get; init; }
    internal string? Timeout { get; init; }
    internal ImmutableArray<string> RuleSet { get; init; }
    internal ImmutableArray<string> QueryType { get; init; }
    internal ImmutableArray<string> PreferredBy { get; init; }
    internal ImmutableArray<string> IpCidr { get; init; }
    internal string? MatchResponse { get; init; }
    internal DnsResultCode? ResponseRcode { get; init; }
    internal DnsOperation? Action { get; init; }
    internal string? Server { get; init; }
    internal string? Tag { get; init; }
    internal bool? Race { get; init; }
    internal bool? DisableOptimisticCache { get; init; }
    internal DnsResultCode? Rcode { get; init; }

    internal bool HasSamePolicy(DnsRulePlan other) =>
        (this with { Rules = default, DefaultInterfaceAddress = default, RuleSet = default, QueryType = default, PreferredBy = default, IpCidr = default })
        == (other with { Rules = default, DefaultInterfaceAddress = default, RuleSet = default, QueryType = default, PreferredBy = default, IpCidr = default })
        && Same(DefaultInterfaceAddress, other.DefaultInterfaceAddress) && Same(RuleSet, other.RuleSet)
        && Same(QueryType, other.QueryType) && Same(PreferredBy, other.PreferredBy) && Same(IpCidr, other.IpCidr)
        && Rules.IsDefault == other.Rules.IsDefault
        && (Rules.IsDefault || Rules.Length == other.Rules.Length && Rules.Zip(other.Rules).All(pair => pair.First.HasSamePolicy(pair.Second)));

    private static bool Same<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefault == right.IsDefault && (left.IsDefault || left.SequenceEqual(right));
}

internal enum DnsOperation { Predefined, Route, Evaluate, Respond }
internal enum DnsResultCode { NoError, NameError, ServerFailure }
