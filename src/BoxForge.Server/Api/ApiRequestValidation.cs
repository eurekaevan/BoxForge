using System.Diagnostics.CodeAnalysis;
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
            || name.Contains('/')
            || name.Contains('\\')
            || name.Any(char.IsControl))
        {
            return false;
        }

        int runeCount = name.EnumerateRunes().Count();
        return runeCount is >= 1 and <= ApiLimits.MaxConfigurationNameRunes;
    }
}
