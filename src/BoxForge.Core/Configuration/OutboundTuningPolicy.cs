using BoxForge.Models;
using BoxForge.Models.Singbox;

namespace BoxForge.Configuration;

public static class OutboundTuningPolicy
{
    private const string ConnectTimeout = "5s";
    private const string DesktopTcpKeepAlive = "1m";
    private const string DesktopTcpKeepAliveInterval = "30s";
    private const string Hysteria2HopInterval = "30s";
    private const string Hysteria2HopIntervalMax = "60s";
    private const string Hysteria2BbrProfile = "standard";

    public static ProxyOutbound Apply(ProxyOutbound outbound, TargetPlatform platform)
    {
        ProxyOutbound tuned = outbound with
        {
            ConnectTimeout = outbound.ConnectTimeout ?? ConnectTimeout,
            TcpKeepAlive = outbound.TcpKeepAlive
                ?? (platform == TargetPlatform.Android ? null : DesktopTcpKeepAlive),
            TcpKeepAliveInterval = outbound.TcpKeepAliveInterval
                ?? (platform == TargetPlatform.Android ? null : DesktopTcpKeepAliveInterval)
        };

        return tuned is Hysteria2Outbound hysteria2
            ? hysteria2 with
            {
                HopInterval = hysteria2.HopInterval ?? Hysteria2HopInterval,
                HopIntervalMax = hysteria2.HopIntervalMax
                    ?? (hysteria2.HopInterval is null ? Hysteria2HopIntervalMax : null),
                BbrProfile = hysteria2.BbrProfile
                    ?? (hysteria2.UpMbps is > 0 || hysteria2.DownMbps is > 0
                        ? null : Hysteria2BbrProfile)
            }
            : tuned;
    }
}
