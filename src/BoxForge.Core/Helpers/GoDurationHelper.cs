using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using BoxForge.Exceptions;

namespace BoxForge.Helpers;

internal static partial class GoDurationHelper
{
    [GeneratedRegex(
        @"\G([0-9]+(?:\.[0-9]*)?|\.[0-9]+)(ns|us|µs|μs|ms|s|m|h|d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Components();

    internal static bool IsPositiveDuration(string value) =>
        TryParseDuration(value, out long duration) && duration > 0;

    internal static bool TryNormalizePositiveExtendedDuration(string value, out string normalized)
    {
        normalized = value;
        if (!TryParseDuration(value, out long duration, allowDays: true) || duration <= 0) return false;
        // Integer nanoseconds avoid the target core's floating-point rounding for fractions.
        if (value.Contains('.')) normalized = duration.ToString(CultureInfo.InvariantCulture) + "ns";
        return true;
    }

    public static string ParsePositiveSecondsOrDuration(string value, string field)
    {
        value = value.Trim();
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds is > 0 and <= long.MaxValue / 1_000_000_000)
                return seconds.ToString(CultureInfo.InvariantCulture) + "s";
            throw Invalid(field);
        }

        if (!TryParseDuration(value, out long duration) || duration <= 0) throw Invalid(field);
        return value;
    }

    private static bool TryParseDuration(string value, out long result, bool allowDays = false)
    {
        result = 0;
        // Bound untrusted input before arbitrary-precision fractional parsing.
        if (value.Length is 0 or > 128) return false;
        string unsigned = value.StartsWith('+') ? value[1..] : value;
        if (unsigned.Length == 0) return false;
        if (unsigned == "0") return true;
        BigInteger nanoseconds = 0;
        int consumed = 0;
        foreach (Match component in Components().Matches(unsigned))
        {
            if (component.Index != consumed) return false;
            if (!allowDays && component.Groups[2].Value == "d") return false;
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
                "d" => 86_400_000_000_000,
                _ => throw new InvalidOperationException("Unrecognized duration unit")
            };
            nanoseconds += BigInteger.Parse(parts[0].Length == 0 ? "0" : parts[0], CultureInfo.InvariantCulture) * unit;
            if (parts.Length == 2 && parts[1].Length > 0)
                nanoseconds += BigInteger.Parse(parts[1], CultureInfo.InvariantCulture) * unit
                    / BigInteger.Pow(10, parts[1].Length);
            consumed += component.Length;
            if (nanoseconds > long.MaxValue) return false;
        }
        if (consumed != unsigned.Length) return false;
        result = (long)nanoseconds;
        return true;
    }

    private static NodeParseException Invalid(string field) =>
        NodeParseException.ForField(field, $"字段 '{field}' 必须是正数秒或有效的正 Go duration，且不能超出 int64 纳秒范围");
}
