using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;

namespace BoxForge.Configuration;

internal static class ProfileConfiguration
{
    internal static BoxForgeProfile ResolveProfile(IConfiguration configuration)
    {
        var profile = BoxForgeProfile.Load(configuration["Profile"]);
        bool? enabled = ReadBool(configuration, "Tailscale:Enabled", "TailscaleEnabled");
        bool? android = ReadBool(configuration, "Tailscale:AndroidEnabled", "TailscaleAndroidEnabled");
        bool? api = ReadBool(configuration, "SingboxApi:Enabled", null);
        if (enabled is null && android is null && api is null) return profile;
        return profile.ApplyOverrides(new JsonObject
        {
            ["version"] = 1,
            ["runtime"] = new JsonObject
            {
                ["tailscale"] = new JsonObject
                {
                    ["enabled"] = enabled ?? profile.Runtime.Tailscale.Enabled,
                    ["android_enabled"] = android ?? profile.Runtime.Tailscale.AndroidEnabled
                },
                ["singbox_api"] = new JsonObject { ["enabled"] = api ?? profile.Runtime.SingboxApi.Enabled }
            }
        });
    }

    private static bool? ReadBool(IConfiguration configuration, string nestedKey, string? legacyKey)
    {
        string? value = configuration[nestedKey] ?? (legacyKey is null ? null : configuration[legacyKey]);
        if (value is null) return null;
        if (bool.TryParse(value, out bool parsed)) return parsed;
        throw new FormatException($"配置项 '{nestedKey}' 必须是 true 或 false。");
    }
}
