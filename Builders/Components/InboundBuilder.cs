using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Configuration;

namespace BoxForge.Builders.Components;

public static class InboundBuilder
{
    public static List<Inbound> Build(TargetPlatform platform)
    {
        return
        [
            new Inbound
            {
                Type = "tun",
                Tag = SingboxTags.TunInbound,
                Address = ["172.19.0.1/30", "fd00::1/126"],
                DnsMode = "hijack",
                AutoRoute = true,
                AutoRedirect = platform == TargetPlatform.Linux ? true : null,
                StrictRoute = true,
                Stack = platform == TargetPlatform.Windows ? "mixed" : "system",
                Mtu = platform == TargetPlatform.Android ? 1400 : null
            },
            new Inbound
            {
                Type = "mixed",
                Tag = SingboxTags.MixedInbound,
                Listen = "127.0.0.1",
                ListenPort = 8848
            }
        ];
    }
}
