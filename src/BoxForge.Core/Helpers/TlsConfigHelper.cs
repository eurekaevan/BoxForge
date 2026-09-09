using BoxForge.Models.Singbox;
using BoxForge.Models.Clash;
using BoxForge.Exceptions;

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
        var realityOptions = node.GetObject("reality-opts")
            ?? node.GetObject("reality_opts");
        bool isReality = realityOptions != null;

        if (!forceTls && !isTls && !isReality) return null;

        OutboundReality? realityConfig = null;
        if (realityOptions != null)
        {
            string publicKey = realityOptions.GetString("public-key")
                ?? realityOptions.GetString("public_key")
                ?? throw new NodeParseException(
                    "Reality 缺失必填字段或为空: public-key");
            if (!IsValidRealityPublicKey(publicKey))
            {
                throw new NodeParseException(
                    "Reality public-key 必须是 32 字节的 Base64URL 公钥");
            }

            string? rawShortId = realityOptions.GetRawString("short-id")
                ?? realityOptions.GetRawString("short_id");
            if (rawShortId == null)
            {
                throw new NodeParseException(
                    "Reality 缺失必填字段: short-id");
            }

            string shortId = rawShortId.Trim();
            if (shortId.Length > 16
                || shortId.Length % 2 != 0
                || shortId.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new NodeParseException(
                    "Reality short-id 必须是 0 到 8 字节的偶数位十六进制字符串");
            }

            realityConfig = new OutboundReality
            {
                Enabled = true,
                PublicKey = publicKey,
                ShortId = shortId
            };
        }

        string? fingerprint = supportsUtls
            ? node.GetString("client-fingerprint")
                ?? node.GetString("client_fingerprint")
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
            Reality = realityConfig
        };
    }

    private static bool IsValidRealityPublicKey(string publicKey)
    {
        if (publicKey.Length != 43
            || publicKey.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_')))
        {
            return false;
        }

        string normalized = publicKey
            .Replace('-', '+')
            .Replace('_', '/');
        int paddingLength = (4 - normalized.Length % 4) % 4;
        normalized = normalized.PadRight(
            normalized.Length + paddingLength,
            '=');

        try
        {
            return Convert.FromBase64String(normalized).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
