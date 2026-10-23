using BoxForge.Models.Singbox;
using BoxForge.Configuration;
using BoxForge.Models.Intermediate;

namespace BoxForge.Builders.Components;

internal static class InboundBuilder
{
    internal static List<Inbound> Emit(TunPlan plan)
    {
        return
        [
            new Inbound
            {
                Type = "tun",
                Tag = SingboxTags.TunInbound,
                // Capture both address families; routing applies the selected IPv6 policy.
                Address = plan.Addresses.ToList(),
                DnsMode = "hijack",
                AutoRoute = true,
                AutoRedirect = plan.AutoRedirect,
                MultiQueue = plan.MultiQueue,
                StrictRoute = plan.StrictRoute
            }
        ];
    }
}
