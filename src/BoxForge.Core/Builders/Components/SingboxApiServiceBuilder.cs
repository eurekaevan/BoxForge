using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public sealed class SingboxApiServiceBuilder(
    IOptions<SingboxApiOptions> options)
{
    private const string ListenAddress = "127.0.0.1";
    private const int ListenPort = 9090;
    private readonly SingboxApiOptions apiOptions = options.Value;

    public List<SingboxService> Build()
    {
        if (!apiOptions.Enabled)
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
                    HttpClient = HttpClientTags.RuleSetDirect
                }
            }
        ];
    }
}
