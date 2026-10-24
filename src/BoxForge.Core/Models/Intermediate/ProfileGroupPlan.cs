using BoxForge.Exceptions;

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
        var diagnostics = new List<ConfigDiagnostic>();
        CollectDiagnostics(diagnostics);
        ConfigValidationException.ThrowIfAny(diagnostics);
    }

    internal void CollectDiagnostics(List<ConfigDiagnostic> diagnostics)
    {
        var leaves = Leaves.ToHashSet(StringComparer.Ordinal);
        var targets = new HashSet<string>(leaves, StringComparer.Ordinal);
        Declare(DirectName, "plan.groups.direct_name");
        Declare(Main.Name, "plan.groups.main.name");
        for (int index = 0; index < Regions.Count; index++)
            Declare(Regions[index].Name, $"plan.groups.regions[{index}].name");
        for (int index = 0; index < Probes.Count; index++)
            Declare(Probes[index].Name, $"plan.groups.probes[{index}].name");
        for (int index = 0; index < Services.Count; index++)
            Declare(Services[index].Name, $"plan.groups.services[{index}].name");
        ValidateSelection(Main, "plan.groups.main");
        for (int index = 0; index < Regions.Count; index++)
            ValidateSelection(Regions[index], $"plan.groups.regions[{index}]");
        for (int index = 0; index < Probes.Count; index++)
        {
            ProbeGroupPlan group = Probes[index];
            string path = $"plan.groups.probes[{index}].candidates";
            if (group.Candidates.Count < 2)
                diagnostics.Add(new("IR008", path, "探测组至少需要两个叶节点。"));
            for (int candidate = 0; candidate < group.Candidates.Count; candidate++)
                if (!leaves.Contains(group.Candidates[candidate]))
                    diagnostics.Add(new("IR002", $"{path}[{candidate}]", "探测组引用了未声明的叶节点。"));
        }
        for (int index = 0; index < Services.Count; index++)
            ValidateSelection(Services[index], $"plan.groups.services[{index}]");

        void Declare(string name, string path)
        {
            if (string.IsNullOrWhiteSpace(name) || !targets.Add(name))
                diagnostics.Add(new("IR009", path, "组或节点声明必须具有非空且唯一的名称。"));
        }

        void ValidateSelection(SelectionGroupPlan group, string path)
        {
            if (!group.Candidates.Contains(group.Preferred, StringComparer.Ordinal))
                diagnostics.Add(new("IR010", path + ".preferred", "首选目标必须属于候选列表。"));
            for (int index = 0; index < group.Candidates.Count; index++)
                if (!targets.Contains(group.Candidates[index]))
                    diagnostics.Add(new("IR003", $"{path}.candidates[{index}]", "选择组引用了未声明的目标。"));
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
    string Interval);
