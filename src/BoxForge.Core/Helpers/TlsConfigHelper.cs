using BoxForge.Converters;
using BoxForge.Models.Intermediate;

namespace BoxForge.Helpers;

internal static class TlsConfigHelper
{
    internal static TlsSettings? ExtractSettings(
        NodeFieldReader node,
        string server,
        bool forceTls = false,
        bool supportsUtls = true)
    {
        bool isTls = node.Bool("tls");
        var realityOptions = node.OptionalObject("reality-opts");
        bool isReality = realityOptions != null;

        string realityField = node.PresentField("reality-opts") ?? "reality-opts";
        if (!forceTls && !isTls && !isReality) return null;
        if (isReality && !forceTls && !isTls)
            throw node.Error(realityField, "字段 'reality-opts' 要求显式启用 tls，不能隐式改变来源 TLS 设置");

        RealitySettings? realityConfig = null;
        if (realityOptions != null)
        {
            string publicKey = realityOptions.String("public-key")
                ?? throw realityOptions.Error("public-key",
                    "Reality 缺失必填字段或为空: public-key");
            if (!IsValidRealityPublicKey(publicKey))
            {
                throw realityOptions.Error("public-key",
                    "Reality public-key 必须是 32 字节的 Base64URL 公钥");
            }

            string? rawShortId = realityOptions.RawString("short-id");
            if (rawShortId == null)
            {
                throw realityOptions.Error("short-id",
                    "Reality 缺失必填字段: short-id");
            }

            string shortId = rawShortId.Trim();
            if (shortId.Length > 16
                || shortId.Length % 2 != 0
                || shortId.Any(character => !Uri.IsHexDigit(character)))
            {
                throw realityOptions.Error("short-id",
                    "Reality short-id 必须是 0 到 8 字节的偶数位十六进制字符串");
            }

            realityConfig = new RealitySettings(publicKey, shortId);
        }

        string? fingerprint = supportsUtls
            ? node.RawString("client-fingerprint")
            : null;
        string? effectiveFingerprint = null;
        if (fingerprint is not (null or "" or "none"))
        {
            if (!IsSupportedFingerprint(fingerprint))
                throw node.Error(node.PresentField("client-fingerprint") ?? "client-fingerprint", "字段 'client-fingerprint' 无法精确映射到目标核心；不支持旧版本、历史别名、random 或未知指纹");
            effectiveFingerprint = fingerprint;
        }
        if (isReality && effectiveFingerprint == null)
            throw node.Error(realityField, "字段 'reality-opts' 要求有效的 client-fingerprint；空值或 none 不支持 Reality");

        return new TlsSettings(
            node.String("sni") ?? server,
            node.OptionalBool("skip-cert-verify"),
            node.Value("alpn") is IEnumerable<object?> alpn
                ? Array.AsReadOnly(alpn.Cast<string>().ToArray()) : null,
            effectiveFingerprint,
            realityConfig);
    }

    internal static bool IsSupportedFingerprint(string fingerprint) =>
        fingerprint is "chrome" or "firefox" or "safari" or "ios" or "android"
            or "edge" or "360" or "qq" or "randomized";

    internal static string? ValidateAlpn(object? value) =>
        value is IEnumerable<object?> protocols
            && protocols.All(protocol => protocol is string { Length: > 0 } text
                && System.Text.Encoding.UTF8.GetByteCount(text) <= 255)
            ? null
            : "必须是 TLS 协议字符串列表，每项为 1 到 255 字节；保持源顺序和值";

    private static bool IsValidRealityPublicKey(string publicKey)
    {
        if (publicKey.Length != 43
            || publicKey.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_')))
        {
            return false;
        }

        // A 43-character Base64URL key always needs one padding character.
        string normalized = publicKey.Replace('-', '+').Replace('_', '/') + "=";

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
