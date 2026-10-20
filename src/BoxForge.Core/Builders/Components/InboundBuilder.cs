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
                // The IPv6 prefix is capture-only: auto_route needs an IPv6
                // family on the TUN so IPv6 cannot bypass the global rejects.
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
