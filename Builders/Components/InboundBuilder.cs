using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public class InboundBuilder
{
    public List<Inbound> Build(TargetPlatform platform)
    {
        return
        [
            new Inbound
            {
                Type = "tun",
                Tag = "tun-in",
                Address = ["172.19.0.1/30", "fd00::1/126"],
                DnsMode = "hijack",
                AutoRoute = true,
                AutoRedirect = platform == TargetPlatform.Linux ? true : null,
                StrictRoute = true,
                Stack = platform switch
                {
                    TargetPlatform.Windows => "mixed",
                    TargetPlatform.Linux => "system",
                    TargetPlatform.Android => "system",
                    _ => "system"
                },
                Mtu = platform == TargetPlatform.Android ? 1400 : null
            },
            new Inbound
            {
                Type = "mixed",
                Tag = "mixed-in",
                Listen = "127.0.0.1",
                ListenPort = 8848
            }
        ];
    }
}
