using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public class ServiceBuilder
{
    public List<SingboxService> Build(TargetPlatform platform)
    {
        if (platform == TargetPlatform.Android)
        {
            return [];
        }

        return
        [
            new SingboxApiService
            {
                Tag = "api",
                Listen = "127.0.0.1",
                ListenPort = 9090,
                Secret = "127001",
                Dashboard = new SingboxApiDashboard
                {
                    Enabled = true,
                    Path = platform == TargetPlatform.Windows
                        ? "ui"
                        : "/etc/sing-box/ui",
                    HttpClient = SingboxOptions.RuleSetHttpClientTag
                }
            }
        ];
    }
}
