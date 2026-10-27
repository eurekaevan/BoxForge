using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace BoxForge.Helpers;

/// <summary>Validates configuration names used as cross-platform directory or archive entries.</summary>
public static class ConfigurationNameValidator
{
    public static bool IsValid(
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
        return runeCount is >= 1 and <= 100;
    }
}
