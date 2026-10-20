using BoxForge.Models.Intermediate;

namespace BoxForge.Configuration;

internal static class OutboundTuningPolicy
{
    private static readonly Hysteria2HopInterval DefaultHysteria2Hop = new(30, 60);
    private const Hysteria2BbrProfile DefaultHysteria2Bbr = Hysteria2BbrProfile.Standard;

    internal static ProxyNode Apply(ProxyNode node, PlatformProfile profile)
    {
        ProxyNode tuned = node with
        {
            Connection = node.Connection with
            {
                ConnectTimeout = node.Connection.ConnectTimeout ?? profile.ConnectTimeout,
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

}
