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
        services.AddSingleton<HttpClient>();
        services.AddSingleton<INodeEnrichmentDatabaseSource, NodeEnrichmentDatabaseSource>();
        services.AddSingleton<IGeoLite2CityReaderFactory, MaxMindGeoLite2CityReaderFactory>();
        services.AddSingleton<ICityDatabase, GeoLite2CityDatabase>();
        services.AddSingleton<IHostAddressResolver, HostAddressResolver>();

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
