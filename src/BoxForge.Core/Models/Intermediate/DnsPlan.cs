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
    internal const string SequentialPrimaryTimeout = "2s";

    // Expanded rules are authoritative; resolver blocks are derived, never stored.
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
        if (Rules.IsDefaultOrEmpty)
            diagnostics.Add(new("IR008", "plan.dns.rules", "DNS 计划必须包含显式规则。"));
        if (TailscaleEnabled)
            resolvers.Add(SingboxTags.TailscaleDns);
        var responses = new HashSet<string>(StringComparer.Ordinal);
        if (!Rules.IsDefault)
            for (int index = 0; index < Rules.Length; index++)
                ValidateRule(Rules[index], $"plan.dns.rules[{index}]");
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

    // Derived ranges only: the general SERVFAIL closes each resolver block.
    internal IEnumerable<(int Start, int End)> ResolverBlocks()
    {
        if (Rules.IsDefault) yield break;
        int start = -1;
        for (int index = 0; index < Rules.Length; index++)
        {
            DnsRulePlan rule = Rules[index];
            if (start < 0 && rule.Action == DnsOperation.Evaluate) start = index;
            if (start >= 0 && rule.Action == DnsOperation.Predefined
                && rule.Rcode == DnsResultCode.ServerFailure && rule.QueryType.IsDefault)
            {
                yield return (start, index);
                start = -1;
            }
        }
    }

    private void ValidatePolicy(List<ConfigDiagnostic> diagnostics)
    {
        int prefix = 0;
        if (AddressFamily != AddressFamilyPolicy.DualStack)
        {
            var guard = new DnsRulePlan { QueryType = ["AAAA"], Action = DnsOperation.Predefined, Rcode = DnsResultCode.NoError };
            if (AddressFamily.UsesInterfaceGuard())
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
        RequireAt(prefix++, new() { RuleSet = [RuleSetTags.Ads], Action = DnsOperation.Predefined, Rcode = DnsResultCode.NameError });
        string?[] queryTypes = SelectionMode == DnsResolverSelectionMode.ParallelFastest
            ? AddressFamily == AddressFamilyPolicy.Ipv4Only ? ["A", null] : ["A", "AAAA", null]
            : [null];
        int next = prefix;
        bool hasGlobal = false;
        foreach (var (start, end) in ResolverBlocks())
        {
            ImmutableArray<string> scope = Rules[start].RuleSet;
            bool valid = start == next && !hasGlobal && (scope.IsDefault || !scope.IsEmpty);
            hasGlobal = scope.IsDefault;
            int cursor = start;
            string? primaryServer = null, secondaryServer = null;
            foreach (string? queryType in queryTypes)
            {
                int terminal = cursor;
                while (terminal <= end && Rules[terminal].Action != DnsOperation.Predefined) terminal++;
                if (terminal > end) { valid = false; break; }
                valid &= ValidateWindow(cursor, terminal, scope, queryType, ref primaryServer, ref secondaryServer);
                cursor = terminal + 1;
            }
            valid &= cursor == end + 1;
            if (!valid)
                diagnostics.Add(new("IR011", $"plan.dns.rules[{start}]", "DNS 解析块的范围、查询顺序、主备绑定、响应优先级或终止规则无效。"));
            next = end + 1;
        }
        if (!hasGlobal || next != Rules.Length)
            diagnostics.Add(new("IR011", "plan.dns.rules", "DNS 规则必须由完整解析块组成，并以唯一的全局解析块结束。"));

        void RequireAt(int index, DnsRulePlan expected)
        {
            if (index >= Rules.Length || !Rules[index].HasSamePolicy(expected))
                diagnostics.Add(new("IR011", $"plan.dns.rules[{index}]", "DNS 必要守卫、控制面或屏蔽规则缺失或不符合策略。"));
        }
    }

    private bool ValidateWindow(int start, int end, ImmutableArray<string> scope, string? queryType,
        ref string? primaryServer, ref string? secondaryServer)
    {
        ImmutableArray<string> query = queryType is null ? default : [queryType];
        bool parallel = SelectionMode == DnsResolverSelectionMode.ParallelFastest;
        bool addressQuery = queryType is not null;
        var evaluations = Enumerable.Range(start, end - start)
            .Where(index => Rules[index].Action == DnsOperation.Evaluate).ToArray();
        if (evaluations.Length != 2 || evaluations[0] != start) return false;
        DnsRulePlan primary = Rules[evaluations[0]], secondary = Rules[evaluations[1]];
        primaryServer ??= primary.Server;
        secondaryServer ??= secondary.Server;
        bool valid = primary.Server == primaryServer && secondary.Server == secondaryServer
            && primary.Server != secondary.Server;
        for (int priority = 0; priority < evaluations.Length; priority++)
        {
            DnsRulePlan rule = Rules[evaluations[priority]];
            valid &= rule.HasSamePolicy(new()
            {
                RuleSet = scope,
                QueryType = query,
                Action = DnsOperation.Evaluate,
                Server = rule.Server,
                Tag = rule.Tag,
                DisableOptimisticCache = addressQuery ? true : null,
                Timeout = !parallel && priority == 0 ? SequentialPrimaryTimeout : null
            });
        }
        var responses = new HashSet<(int Priority, DnsResultCode? Code, bool Race)>();
        int previousRank = -1;
        for (int index = start; index < end; index++)
        {
            DnsRulePlan rule = Rules[index];
            if (rule.Action == DnsOperation.Evaluate) continue;
            int priority = rule.MatchResponse == primary.Tag ? 0 : rule.MatchResponse == secondary.Tag ? 1 : -1;
            bool positive = rule.ResponseRcode == DnsResultCode.NoError;
            bool negative = rule.ResponseRcode == DnsResultCode.NameError;
            bool race = parallel && positive && rule.Race == true;
            if (priority < 0 || (!positive && !negative)) return false;
            // Races precede empty fallbacks, which precede ordered negative answers.
            int rank = parallel
                ? race ? 0 : (positive ? 1 : 3) + priority
                : priority * 2 + (negative ? 1 : 0);
            valid &= responses.Add((priority, rule.ResponseRcode, race)) && rank >= previousRank;
            previousRank = rank;
            valid &= parallel ? index > evaluations[1]
                : priority == 0 ? index < evaluations[1] : index > evaluations[1];
            valid &= rule.HasSamePolicy(new()
            {
                RuleSet = race && addressQuery ? default : scope,
                QueryType = query,
                IpCidr = race && addressQuery ? [queryType == "A" ? "0.0.0.0/0" : "::/0"] : default,
                MatchResponse = priority == 0 ? primary.Tag : secondary.Tag,
                ResponseRcode = rule.ResponseRcode,
                Action = DnsOperation.Respond,
                Race = race ? true : null
            });
            // General queries have no separate empty-answer fallback after the race.
            if (parallel && positive && !race && !addressQuery) valid = false;
        }
        int expectedResponses = parallel && addressQuery ? 6 : 4;
        valid &= responses.Count == expectedResponses;
        return valid && Rules[end].HasSamePolicy(new()
        {
            RuleSet = scope,
            QueryType = query,
            Action = DnsOperation.Predefined,
            Rcode = DnsResultCode.ServerFailure
        });
    }
}

internal sealed record DnsHttpsResolverPlan(string Name, string Address, string ServerName, string? Detour);

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
