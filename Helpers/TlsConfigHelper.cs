using BoxForge.Models.Singbox;
using BoxForge.Models.Clash;

namespace BoxForge.Helpers;

public static class TlsConfigHelper
{
    public static OutboundTls? Extract(
        ClashProxyNode node,
        string server,
        bool forceTls = false,
        bool supportsUtls = true)
    {
        bool isTls = node.GetBool("tls");
        var realityOptions = node.GetObject("reality-opts");
        bool isReality = realityOptions != null;

        if (!forceTls && !isTls && !isReality) return null;

        OutboundReality? realityConfig = null;
        if (realityOptions != null)
        {
            realityConfig = new OutboundReality
            {
                Enabled = true,
                PublicKey = realityOptions.GetRawString("public-key") ?? "",
                ShortId = realityOptions.GetRawString("short-id") ?? ""
            };
        }

        string? fingerprint = supportsUtls
            ? node.GetString("client-fingerprint")
            : null;

        return new OutboundTls
        {
            Enabled = true,
            ServerName = node.GetString("sni") ?? node.GetString("servername") ?? server,
            Insecure = node.GetNullableBool("skip-cert-verify"),
            Utls = string.IsNullOrWhiteSpace(fingerprint)
                ? null
                : new Utls
                {
                    Enabled = true,
                    Fingerprint = fingerprint
                },
            Alpn = node.GetStringList("alpn"),
            Reality = realityConfig
        };
    }
}
