using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;
using System.Globalization;

namespace BoxForge.Configuration;

public static class OutboundTuningPolicy
{
    private const string ConnectTimeout = "5s";
    private static readonly Hysteria2HopInterval DefaultHysteria2Hop = new(30, 60);
    private const Hysteria2BbrProfile DefaultHysteria2Bbr = Hysteria2BbrProfile.Standard;

    internal static ProxyNode Apply(ProxyNode node, PlatformProfile profile)
    {
        ProxyNode tuned = node with
        {
            Connection = node.Connection with
            {
                ConnectTimeout = node.Connection.ConnectTimeout ?? ConnectTimeout,
                TcpKeepAlive = node.Connection.TcpKeepAlive ?? profile.TcpKeepAlive,
                TcpKeepAliveInterval = node.Connection.TcpKeepAliveInterval ?? profile.TcpKeepAliveInterval
            }
        };
        ProxyNode result = tuned is Hysteria2Node hysteria2
            ? hysteria2 with
            {
                TuningHopInterval = hysteria2.HopInterval.Value is null
                    ? hysteria2.TuningHopInterval ?? DefaultHysteria2Hop : null,
                TuningBbrProfile = hysteria2.BbrProfile.Value is null
                    && !(hysteria2.UploadMegabitsPerSecond.Value > 0 || hysteria2.DownloadMegabitsPerSecond.Value > 0)
                    ? hysteria2.TuningBbrProfile ?? DefaultHysteria2Bbr : null
            }
            : tuned;
        result.Validate();
        return result;
    }

    public static ProxyOutbound Apply(ProxyOutbound outbound, PlatformProfile profile)
    {
        ProxyOutbound tuned = outbound with
        {
            ConnectTimeout = outbound.ConnectTimeout ?? ConnectTimeout,
            TcpKeepAlive = outbound.TcpKeepAlive
                ?? profile.TcpKeepAlive,
            TcpKeepAliveInterval = outbound.TcpKeepAliveInterval
                ?? profile.TcpKeepAliveInterval
        };

        return tuned is Hysteria2Outbound hysteria2
            ? hysteria2 with
            {
                HopInterval = hysteria2.HopInterval ?? DefaultHysteria2Hop.MinimumSeconds.ToString(CultureInfo.InvariantCulture) + "s",
                HopIntervalMax = hysteria2.HopIntervalMax
                    ?? (hysteria2.HopInterval is null ? DefaultHysteria2Hop.MaximumSeconds?.ToString(CultureInfo.InvariantCulture) + "s" : null),
                BbrProfile = hysteria2.BbrProfile
                    ?? (hysteria2.UpMbps is > 0 || hysteria2.DownMbps is > 0
                        ? null : DefaultHysteria2Bbr.ToString().ToLowerInvariant())
            }
            : tuned;
    }
}
