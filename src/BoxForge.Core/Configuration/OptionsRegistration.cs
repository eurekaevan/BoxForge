using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Configuration;

public static class OptionsRegistration
{
    public static IServiceCollection AddBoxForgeOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TailscaleOptions>(options =>
        {
            options.Enabled = ReadBool(
                configuration,
                "Tailscale:Enabled",
                "TailscaleEnabled",
                false);
            options.AndroidEnabled = ReadBool(
                configuration,
                "Tailscale:AndroidEnabled",
                "TailscaleAndroidEnabled",
                false);
        });
        services.Configure<SingboxApiOptions>(options =>
        {
            options.Enabled = ReadBool(
                configuration,
                "SingboxApi:Enabled",
                null,
                false);
        });

        return services;
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string nestedKey,
        string? legacyKey,
        bool defaultValue)
    {
        var value = configuration[nestedKey]
            ?? (legacyKey == null ? null : configuration[legacyKey]);
        if (value == null)
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new FormatException(
            $"配置项 '{nestedKey}' 必须是 true 或 false。");
    }
}
