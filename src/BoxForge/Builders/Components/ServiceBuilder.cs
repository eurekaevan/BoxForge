using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Options;

namespace BoxForge.Builders.Components;

public class ServiceBuilder(IOptions<SingboxOptions> options)
{
    private readonly SingboxOptions singbox = options.Value;

    public List<SingboxService> Build(
        TargetPlatform platform,
        string generatedSecret)
    {
        if (platform != TargetPlatform.Linux)
        {
            return [];
        }

        return
        [
            new SingboxApiService
            {
                Tag = SingboxTags.ApiService,
                Listen = "127.0.0.1",
                ListenPort = 9090,
                Secret = string.IsNullOrWhiteSpace(singbox.ApiSecret)
                    ? generatedSecret
                    : singbox.ApiSecret,
                Dashboard = new SingboxApiDashboard
                {
                    Enabled = true,
                    Path = "/etc/sing-box/dashboard",
                    HttpClient = SingboxOptions.RuleSetHttpClientTag
                }
            }
        ];
    }
}
