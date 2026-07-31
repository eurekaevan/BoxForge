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
        services.Configure<GitHubOptions>(options =>
        {
            options.Owner = Read(configuration, "GitHub:Owner", "GitHubOwner", "");
            options.Token = Read(configuration, "GitHub:Token", "GitHubToken", "");
            options.Repository = Read(
                configuration,
                "GitHub:Repository",
                "RepoName",
                "BoxVault");
            options.SourceFolder = Read(
                configuration,
                "GitHub:SourceFolder",
                "SubconfigsFolder",
                "clashConfigs");
        });

        services.Configure<OutputOptions>(options =>
        {
            options.BaseFolder = Read(
                configuration,
                "Output:BaseFolder",
                "OutputBaseFolder",
                "singboxConfigs");
            options.LocalFile = Read(
                configuration,
                "Output:LocalFile",
                "LocalOutputFile",
                "config.json");
        });

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

        services.AddSingleton<IValidateOptions<GitHubOptions>, GitHubOptionsValidator>();
        services.AddSingleton<IValidateOptions<OutputOptions>, OutputOptionsValidator>();
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
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
