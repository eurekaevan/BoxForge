using SubConvert.Models.Singbox;
using SubConvert.Extensions;

namespace SubConvert.Helpers;

public static class TlsConfigHelper
{
    public static OutboundTls? Extract(Dictionary<string, object> p, string server, bool forceTls = false)
    {
        bool isTls = p.GetBool("tls");
        bool isReality = p.ContainsKey("reality-opts");

        if (!forceTls && !isTls && !isReality) return null;

        OutboundReality? realityConfig = null;
        if (isReality && p.TryGetValue("reality-opts", out var ro) && ro is Dictionary<object, object> realityOpts)
        {
            string? pk = realityOpts.TryGetValue("public-key", out var pkObj) ? pkObj?.ToString() : null;
            string? sid = realityOpts.TryGetValue("short-id", out var sidObj) ? sidObj?.ToString() : null;

            realityConfig = new OutboundReality
            {
                Enabled = true,
                PublicKey = pk ?? "",
                ShortId = sid ?? ""
            };
        }

        string? fingerprint = p.GetString("client-fingerprint");

        return new OutboundTls
        {
            Enabled = true,
            ServerName = p.GetString("sni") ?? p.GetString("servername") ?? server,
            Insecure = p.GetNullableBool("skip-cert-verify"),
            Utls = string.IsNullOrWhiteSpace(fingerprint)
                ? null
                : new Utls
                {
                    Enabled = true,
                    Fingerprint = fingerprint
                },
            Alpn = p.GetStringList("alpn") ?? ["h2", "http/1.1"],
            MinVersion = "1.3",
            Reality = realityConfig
        };
    }
}