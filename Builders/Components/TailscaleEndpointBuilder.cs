using Microsoft.Extensions.Options;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public sealed class TailscaleEndpointBuilder(IOptions<TailscaleOptions> options)
{
    private readonly TailscaleOptions tailscaleOptions = options.Value;

    public List<Endpoint> Build(TargetPlatform platform)
    {
        if (!tailscaleOptions.Enabled)
        {
            return [];
        }

        return
        [
            new TailscaleEndpoint
            {
                Tag = SingboxTags.TailscaleEndpoint,
                DomainResolver = SingboxTags.BootstrapDns,
                StateDirectory = SingboxTags.TailscaleStateDirectory,
                AcceptRoutes = true,
                TaildropDirectory = GetTaildropDirectory(platform)
            }
        ];
    }

    private static string GetTaildropDirectory(TargetPlatform platform) =>
        platform switch
        {
            TargetPlatform.Android => "Taildrop",
            TargetPlatform.Windows => "$USERPROFILE\\Downloads\\Taildrop",
            TargetPlatform.Linux => "$HOME/Downloads/Taildrop",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
        };
}
