using SubConvert.Models.Singbox;
using SubConvert.Models.Clash;

namespace SubConvert.Helpers;

public static class TlsConfigHelper
{
    public static OutboundTls? Extract(ClashProxyNode node, string server, bool forceTls = false)
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

        string? fingerprint = node.GetString("client-fingerprint");

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
            Alpn = node.GetStringList("alpn") ?? ["h2", "http/1.1"],
            MinVersion = "1.3",
            Reality = realityConfig
        };
    }
}
