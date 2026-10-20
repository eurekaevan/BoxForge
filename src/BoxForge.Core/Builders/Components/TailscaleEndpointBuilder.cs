using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

internal sealed class TailscaleEndpointBuilder(BoxForgeProfile profile)
{
    internal (bool Enabled, string TaildropDirectory) Plan(TargetPlatform platform, BoxForgeProfile? effectiveProfile = null)
    {
        BoxForgeProfile selected = effectiveProfile ?? profile;
        PlatformProfile settings = selected.GetPlatform(platform);
        return (selected.Runtime.Tailscale.IsEnabled(settings), settings.TaildropDirectory);
    }

    internal static List<Endpoint> Emit(string taildropDirectory, bool enabled)
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
