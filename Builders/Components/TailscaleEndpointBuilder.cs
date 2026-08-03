using Microsoft.Extensions.Options;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;

namespace BoxForge.Builders.Components;

public class TailscaleEndpointBuilder(IOptions<TailscaleOptions> options)
{
    private readonly TailscaleOptions tailscaleOptions = options.Value;

    public List<Endpoint> Build()
    {
        if (!tailscaleOptions.Enabled)
        {
            return [];
        }

        return
        [
            new TailscaleEndpoint
            {
                Tag = tailscaleOptions.Tag,
                DomainResolver = "bootstrap",
                StateDirectory = NullIfWhiteSpace(tailscaleOptions.StateDirectory),
                ControlUrl = NullIfWhiteSpace(tailscaleOptions.ControlUrl),
                Hostname = NullIfWhiteSpace(tailscaleOptions.Hostname),
                AcceptRoutes = tailscaleOptions.AcceptRoutes,
                ExitNode = NullIfWhiteSpace(tailscaleOptions.ExitNode),
                ExitNodeAllowLanAccess = string.IsNullOrWhiteSpace(tailscaleOptions.ExitNode)
                    ? null
                    : tailscaleOptions.ExitNodeAllowLanAccess
            }
        ];
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
