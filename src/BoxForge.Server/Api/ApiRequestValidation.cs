using System.Diagnostics.CodeAnalysis;
using System.Text;
using BoxForge.Models;

namespace BoxForge.Server.Api;

internal static class ApiRequestValidation
{
    public static bool TryParsePlatforms(
        IReadOnlyList<string?>? values,
        out IReadOnlyList<TargetPlatform> platforms)
    {
        if (values is not { Count: > 0 })
        {
            platforms = [];
            return false;
        }

        var parsed = new List<TargetPlatform>(values.Count);
        var seen = new HashSet<TargetPlatform>();
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !Enum.TryParse(value, ignoreCase: true, out TargetPlatform platform)
                || !Enum.IsDefined(platform)
                || !string.Equals(value.Trim(), platform.ToString(), StringComparison.OrdinalIgnoreCase)
                || !seen.Add(platform))
            {
                platforms = [];
                return false;
            }

            parsed.Add(platform);
        }

        platforms = parsed;
        return true;
    }

    public static bool IsValidConfigurationName(
        [NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0
            || name.Any(char.IsControl)
            || name.EndsWith('.')
            || name.EndsWith(' ')
            || Encoding.UTF8.GetByteCount(name) > 255)
        {
            return false;
        }

        string deviceName = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (deviceName.Length == 4
                && (deviceName.StartsWith("COM", StringComparison.Ordinal)
                    || deviceName.StartsWith("LPT", StringComparison.Ordinal))
                && (deviceName[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
        {
            return false;
        }

        int runeCount = name.EnumerateRunes().Count();
        return runeCount is >= 1 and <= ApiLimits.MaxConfigurationNameRunes;
    }
}
