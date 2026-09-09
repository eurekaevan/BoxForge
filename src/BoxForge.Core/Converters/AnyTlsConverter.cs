using System.Text.RegularExpressions;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using BoxForge.Exceptions;

namespace BoxForge.Converters;

public sealed class AnyTlsConverter()
    : ProxyConverterBase("AnyTLS", "anytls")
{
    protected override ProxyOutbound ConvertCore(
        ClashProxyNode node,
        string name)
    {
        string server = node.GetRequiredString("server");

        return new AnyTlsOutbound
        {
            Tag = name,
            Server = server,
            ServerPort = node.GetRequiredInt("port"),
            Password = node.GetRequiredString("password"),
            IdleSessionTimeout = ExtractIdleSessionTimeout(node),
            Tls = TlsConfigHelper.Extract(node, server, forceTls: true)
        };
    }

    private static string? ExtractIdleSessionTimeout(ClashProxyNode node)
    {
        string? value = node.GetString("idle-session-timeout")
            ?? node.GetString("idle_session_timeout")
            ?? node.GetString("idle-timeout")
            ?? node.GetString("idle_timeout");
        if (value == null)
        {
            return null;
        }

        if (long.TryParse(value, out long seconds))
        {
            if (seconds > 0)
            {
                return $"{seconds}s";
            }

            throw new NodeParseException(
                "AnyTLS idle-session-timeout 必须大于 0");
        }

        if (Regex.IsMatch(
                value,
                @"^(?:\d+(?:\.\d+)?(?:ns|us|µs|ms|s|m|h))+$",
                RegexOptions.CultureInvariant)
            && value.Any(character => character is >= '1' and <= '9'))
        {
            return value;
        }

        throw new NodeParseException(
            "AnyTLS idle-session-timeout 必须是正数秒或有效的 Go duration");
    }
}
