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
                Tag = tailscaleOptions.Tag,
                DomainResolver = SingboxTags.BootstrapDns,
                StateDirectory = NullIfWhiteSpace(tailscaleOptions.StateDirectory),
                ControlUrl = NullIfWhiteSpace(tailscaleOptions.ControlUrl),
                Hostname = NullIfWhiteSpace(tailscaleOptions.Hostname),
                AcceptRoutes = tailscaleOptions.AcceptRoutes,
                ExitNode = NullIfWhiteSpace(tailscaleOptions.ExitNode),
                ExitNodeAllowLanAccess = string.IsNullOrWhiteSpace(tailscaleOptions.ExitNode)
                    ? null
                    : tailscaleOptions.ExitNodeAllowLanAccess,
                TaildropDirectory = ResolveTaildropDirectory(platform)
            }
        ];
    }

    private string? ResolveTaildropDirectory(TargetPlatform platform)
    {
        if (tailscaleOptions.TaildropDirectory is not null)
        {
            return NullIfWhiteSpace(tailscaleOptions.TaildropDirectory);
        }

        return platform switch
        {
            TargetPlatform.Android => "Taildrop",
            TargetPlatform.Windows => "$USERPROFILE\\Downloads\\Taildrop",
            TargetPlatform.Linux => "$HOME/Downloads/Taildrop",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
        };
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
