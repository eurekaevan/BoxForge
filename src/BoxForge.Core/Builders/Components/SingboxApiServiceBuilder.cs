using BoxForge.Configuration;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

internal static class SingboxApiServiceBuilder
{
    private const string ListenAddress = "127.0.0.1";
    private const int ListenPort = 9090;

    internal static List<SingboxService> Emit(bool enabled)
    {
        if (!enabled)
        {
            return [];
        }

        return
        [
            new ApiService
            {
                Tag = SingboxTags.ApiService,
                Listen = ListenAddress,
                ListenPort = ListenPort,
                AccessControlAllowOrigin =
                [
                    $"http://{ListenAddress}:{ListenPort}",
                    $"http://localhost:{ListenPort}"
                ],
                AccessControlAllowPrivateNetwork = false,
                Dashboard = new ApiDashboardConfig
                {
                    Enabled = true,
                    Path = "dashboard",
                    HttpClient = HttpClientTags.DashboardDirect
                }
            }
        ];
    }
}
