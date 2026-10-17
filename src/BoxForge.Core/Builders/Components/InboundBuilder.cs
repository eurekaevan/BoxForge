using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Configuration;

namespace BoxForge.Builders.Components;

public static class InboundBuilder
{
    public static List<Inbound> Build(TargetPlatform platform)
    {
        PlatformProfile profile = PlatformProfile.For(platform);
        return
        [
            new Inbound
            {
                Type = "tun",
                Tag = SingboxTags.TunInbound,
                // The IPv6 prefix is capture-only: auto_route needs an IPv6
                // family on the TUN so IPv6 cannot bypass the global rejects.
                Address = ["172.19.0.1/30", "fd00::1/126"],
                DnsMode = "hijack",
                AutoRoute = true,
                AutoRedirect = profile.AutoRedirect,
                MultiQueue = profile.MultiQueue,
                StrictRoute = profile.StrictRoute
            }
        ];
    }
}
