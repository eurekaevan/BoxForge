using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders;

internal static class SingboxGroupEmitter
{
    internal static ProfilePlan Emit(ProfileGroupPlan plan)
    {
        return new ProfilePlan(
            Emit(plan.Main),
            plan.Regions.Select(Emit).ToArray(),
            plan.Probes.Select(group => new UrlTestOutbound
            {
                Tag = group.Name,
                Outbounds = [.. group.Candidates],
                Url = group.TestUrl,
                Tolerance = group.Tolerance,
                IdleTimeout = group.IdleTimeout,
                Interval = group.Interval,
                InterruptExistConnections = false
            }).ToArray(),
            plan.Services.Select(Emit).ToArray(),
            new DirectOutbound { Tag = plan.DirectName });
    }

    private static SelectorOutbound Emit(SelectionGroupPlan group) => new()
    {
        Tag = group.Name,
        Outbounds = [.. group.Candidates],
        Default = group.Preferred,
        InterruptExistConnections = true
    };
}
