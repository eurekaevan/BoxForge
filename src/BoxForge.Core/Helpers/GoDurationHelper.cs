using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using BoxForge.Exceptions;

namespace BoxForge.Helpers;

internal static class GoDurationHelper
{
    private static readonly Regex Components = new(
        @"\G([0-9]+(?:\.[0-9]*)?|\.[0-9]+)(ns|us|µs|μs|ms|s|m|h)",
        RegexOptions.CultureInvariant);

    public static string ParsePositiveSecondsOrDuration(string value, string field)
    {
        value = value.Trim();
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds is > 0 and <= long.MaxValue / 1_000_000_000)
                return seconds.ToString(CultureInfo.InvariantCulture) + "s";
            throw Invalid(field);
        }

        // Bound untrusted input before arbitrary-precision fractional parsing.
        if (value.Length is 0 or > 128) throw Invalid(field);
        string unsigned = value.StartsWith('+') ? value[1..] : value;
        BigInteger nanoseconds = 0;
        int consumed = 0;
        foreach (Match component in Components.Matches(unsigned))
        {
            if (component.Index != consumed) throw Invalid(field);
            string amount = component.Groups[1].Value;
            string[] parts = amount.Split('.');
            long unit = component.Groups[2].Value switch
            {
                "ns" => 1,
                "us" or "µs" or "μs" => 1_000,
                "ms" => 1_000_000,
                "s" => 1_000_000_000,
                "m" => 60_000_000_000,
                "h" => 3_600_000_000_000,
                _ => throw Invalid(field)
            };
            nanoseconds += BigInteger.Parse(parts[0].Length == 0 ? "0" : parts[0], CultureInfo.InvariantCulture) * unit;
            if (parts.Length == 2 && parts[1].Length > 0)
                nanoseconds += BigInteger.Parse(parts[1], CultureInfo.InvariantCulture) * unit
                    / BigInteger.Pow(10, parts[1].Length);
            consumed += component.Length;
            if (nanoseconds > long.MaxValue) throw Invalid(field);
        }
        if (consumed != unsigned.Length || nanoseconds <= 0) throw Invalid(field);
        return value;
    }

    private static NodeParseException Invalid(string field) =>
        new($"字段 '{field}' 必须是正数秒或有效的正 Go duration，且不能超出 int64 纳秒范围");
}
