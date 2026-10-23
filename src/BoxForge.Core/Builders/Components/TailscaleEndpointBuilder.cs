using BoxForge.Configuration;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

internal static class TailscaleEndpointBuilder
{
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
