namespace BoxForge.Models.Intermediate;

internal sealed record ProfileGroupPlan(
    IReadOnlyList<string> Leaves,
    string DirectName,
    SelectionGroupPlan Main,
    IReadOnlyList<SelectionGroupPlan> Regions,
    IReadOnlyList<ProbeGroupPlan> Probes,
    IReadOnlyList<SelectionGroupPlan> Services)
{
    internal void Validate()
    {
        var leaves = Leaves.ToHashSet(StringComparer.Ordinal);
        var targets = new HashSet<string>(leaves, StringComparer.Ordinal) { DirectName, Main.Name };
        targets.UnionWith(Regions.Select(group => group.Name));
        targets.UnionWith(Probes.Select(group => group.Name));
        targets.UnionWith(Services.Select(group => group.Name));
        foreach (SelectionGroupPlan group in Regions.Prepend(Main).Concat(Services))
        {
            if (!group.Candidates.Contains(group.Preferred, StringComparer.Ordinal))
                throw new InvalidOperationException($"Group '{group.Name}' must prefer one of its candidates.");
            if (group.Candidates.Any(candidate => !targets.Contains(candidate)))
                throw new InvalidOperationException($"Group '{group.Name}' references an unknown target.");
        }
        foreach (ProbeGroupPlan group in Probes)
        {
            if (group.Candidates.Count < 2 || group.Candidates.Any(candidate => !leaves.Contains(candidate)))
                throw new InvalidOperationException($"Probe group '{group.Name}' requires at least two leaf candidates.");
        }
    }
}

internal sealed record SelectionGroupPlan(string Name, IReadOnlyList<string> Candidates, string Preferred);

internal sealed record ProbeGroupPlan(
    string Name,
    IReadOnlyList<string> Candidates,
    string TestUrl,
    int Tolerance,
    string IdleTimeout,
    string? Interval);
