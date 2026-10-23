namespace BoxForge.Configuration;

public static class SingboxTags
{
    public const string MainProxyGroup = "🚀 PROXIES";
    public const string DirectOutbound = "DIRECT";
    public const string BridgeOutbound = "bridge-out";
    public const string TailscaleEndpoint = "tailscale";
    public const string TailscaleDns = "dns-tailscale";
    public const string TailscaleStateDirectory = "tailscale";
    public const string DirectAliDns = "dns-direct-alidns";
    public const string DirectTencentDns = "dns-direct-tencent";
    public const string ProxyGoogleDns = "dns-proxy-google";
    public const string ProxyCloudflareDns = "dns-proxy-cloudflare";
    public const string TunInbound = "tun-in";
    public const string ApiService = "api";

    internal static string ServiceDns(string serviceId, string resolverId) => $"dns-service-{serviceId}-{resolverId}";
}
