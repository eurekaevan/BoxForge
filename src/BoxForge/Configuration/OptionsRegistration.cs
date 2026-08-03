using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Configuration;

public static class OptionsRegistration
{
    public static IServiceCollection AddBoxForgeOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SingboxOptions>(options =>
        {
            options.MainProxyGroup = Read(
                configuration,
                "Singbox:MainProxyGroup",
                "MainProxyGroup",
                "🚀 PROXIES");
            options.Direct = Read(
                configuration,
                "Singbox:Direct",
                "Direct",
                "DIRECT");
            options.ApiSecret = Read(
                configuration,
                "Singbox:ApiSecret",
                "ApiSecret",
                "");
        });

        services.Configure<TailscaleOptions>(options =>
        {
            options.Enabled = ReadBool(
                configuration,
                "Tailscale:Enabled",
                "TailscaleEnabled",
                false);
            options.Tag = Read(
                configuration,
                "Tailscale:Tag",
                "TailscaleTag",
                "tailscale");
            options.DnsTag = Read(
                configuration,
                "Tailscale:DnsTag",
                "TailscaleDnsTag",
                "tailscale-dns");
            options.StateDirectory = Read(
                configuration,
                "Tailscale:StateDirectory",
                "TailscaleStateDirectory",
                "tailscale");
            options.ControlUrl = Read(
                configuration,
                "Tailscale:ControlUrl",
                "TailscaleControlUrl",
                "");
            options.Hostname = Read(
                configuration,
                "Tailscale:Hostname",
                "TailscaleHostname",
                "");
            options.AcceptRoutes = ReadBool(
                configuration,
                "Tailscale:AcceptRoutes",
                "TailscaleAcceptRoutes",
                true);
            options.ExitNode = Read(
                configuration,
                "Tailscale:ExitNode",
                "TailscaleExitNode",
                "");
            options.ExitNodeAllowLanAccess = ReadBool(
                configuration,
                "Tailscale:ExitNodeAllowLanAccess",
                "TailscaleExitNodeAllowLanAccess",
                false);
        });

        services.AddSingleton<IValidateOptions<SingboxOptions>, SingboxOptionsValidator>();
        services.AddSingleton<IValidateOptions<TailscaleOptions>, TailscaleOptionsValidator>();

        return services;
    }

    private static string Read(
        IConfiguration configuration,
        string nestedKey,
        string legacyKey,
        string defaultValue)
    {
        return configuration[nestedKey]
            ?? configuration[legacyKey]
            ?? defaultValue;
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string nestedKey,
        string legacyKey,
        bool defaultValue)
    {
        var value = configuration[nestedKey] ?? configuration[legacyKey];
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
