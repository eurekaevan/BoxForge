using BoxForge.App;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Converters;
using BoxForge.Parsers;
using BoxForge.Services;
using BoxForge.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Configuration;

public static class ServiceRegistration
{
    public static IServiceCollection AddBoxForge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddBoxForgeOptions(configuration);

        services.AddSingleton<IClashParser, ClashParser>();
        services.AddSingleton<IConfigSerializer, ConfigSerializer>();
        services.AddSingleton<IProxyCacheIdGenerator, ProxyCacheIdGenerator>();
        services.AddSingleton<ISingboxConfigValidator, SingboxConfigValidator>();
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
        }));
        services.AddSingleton<IDbIpDatabaseSource, DbIpDatabaseSource>();
        services.AddSingleton<IDbIpCityReaderFactory, MaxMindDbIpCityReaderFactory>();
        services.AddSingleton<IDbIpCityDatabase, DbIpCityDatabase>();
        services.AddSingleton<IIp2LocationCityClient, Ip2LocationCityClient>();
        services.AddSingleton<IExitIpFetcher, ExitIpFetcher>();
        services.AddSingleton<
            ISingboxExecutableValidator,
            SingboxExecutableValidator>();
        services.AddSingleton<IProbeServerResolver, ProbeServerResolver>();
        services.AddSingleton<ISingboxProcessLauncher, SingboxProcessLauncher>();
        services.AddSingleton<IExitIpDetector, SingboxExitIpDetector>();

        services.AddTransient<IProxyConverter, TrojanConverter>();
        services.AddTransient<IProxyConverter, VlessConverter>();
        services.AddTransient<IProxyConverter, Hysteria2Converter>();
        services.AddTransient<IProxyConverter, ShadowsocksConverter>();
        services.AddTransient<IProxyConverter, AnyTlsConverter>();

        services.AddTransient<NodeCatalogBuilder>();
        services.AddTransient<NodeCityTagEnricher>();
        services.AddTransient<ProfilePlanner>();
        services.AddTransient<TailscaleEndpointBuilder>();
        services.AddTransient<DnsProfileBuilder>();
        services.AddTransient<RouteProfileBuilder>();

        services.AddTransient<ISingboxConfigBuilder, SingboxConfigBuilder>();
        services.AddTransient<ConversionService>();
        services.AddTransient<ILocalGenerationWorkflow, LocalGenerationWorkflow>();
        services.AddTransient<GenerateCommandRunner>();

        return services;
    }
}
