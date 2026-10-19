using Microsoft.Extensions.Options;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public sealed class TailscaleEndpointBuilder(IOptions<TailscaleOptions> options, BoxForgeProfile profile)
{
    private readonly TailscaleOptions tailscaleOptions = options.Value;

    internal (bool Enabled, string TaildropDirectory) Plan(TargetPlatform platform)
    {
        PlatformProfile settings = profile.GetPlatform(platform);
        return (tailscaleOptions.IsEnabled(settings), settings.TaildropDirectory);
    }

    public List<Endpoint> Build(TargetPlatform platform)
    {
        var plan = Plan(platform);
        return Build(plan.TaildropDirectory, plan.Enabled);
    }

    internal static List<Endpoint> Build(string taildropDirectory, bool enabled)
    {
        if (!enabled)
        {
            return [];
        }

        return
        [
            new TailscaleEndpoint
            {
                Tag = SingboxTags.TailscaleEndpoint,
                StateDirectory = SingboxTags.TailscaleStateDirectory,
                AcceptRoutes = true,
                OnDemand = true,
                TaildropDirectory = taildropDirectory
            }
        ];
    }
}
