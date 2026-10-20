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
    internal void Validate()
    {
        if (!Enum.IsDefined(AddressFamily) || !Enum.IsDefined(SelectionMode))
            throw new InvalidOperationException("DNS plan requires a supported address family and selection mode.");
        var resolvers = new HashSet<string>(StringComparer.Ordinal);
        foreach (DnsHttpsResolverPlan resolver in Resolvers)
        {
            if (string.IsNullOrWhiteSpace(resolver.Name) || !resolvers.Add(resolver.Name)
                || string.IsNullOrWhiteSpace(resolver.Address) || string.IsNullOrWhiteSpace(resolver.ServerName))
                throw new InvalidOperationException("DNS plan requires unique, named HTTPS resolvers with an address and TLS name.");
        }
        if (!resolvers.Contains(FinalResolver))
            throw new InvalidOperationException("DNS final resolver must exist in the plan.");
        if (BlockedRuleSets.Count == 0)
            throw new InvalidOperationException("DNS blocking requires rule sets to avoid an unconditional negative response.");
        if (Pools.Count == 0 || Pools[^1].RuleSets is not null)
            throw new InvalidOperationException("DNS plan must end with a global resolver pool.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Pools.Count; index++)
        {
            DnsResolverPoolPlan pool = Pools[index];
            if (string.IsNullOrWhiteSpace(pool.Name) || !names.Add(pool.Name))
                throw new InvalidOperationException("DNS resolver pools require unique names.");
            if (!resolvers.Contains(pool.PrimaryResolver) || !resolvers.Contains(pool.SecondaryResolver))
                throw new InvalidOperationException($"DNS pool '{pool.Name}' references an unknown resolver.");
            if (pool.RuleSets is { Count: 0 } || (index < Pools.Count - 1 && pool.RuleSets is null))
                throw new InvalidOperationException("Scoped DNS pools must precede the global pool and have rule sets.");
        }
    }
}

internal sealed record DnsHttpsResolverPlan(string Name, string Address, string ServerName, string? Detour);

internal sealed record DnsResolverPoolPlan(
    string Name, IReadOnlyList<string>? RuleSets, string PrimaryResolver, string SecondaryResolver);
