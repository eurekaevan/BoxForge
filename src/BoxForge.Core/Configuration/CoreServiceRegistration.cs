using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Converters;
using BoxForge.Engine;
using BoxForge.Parsers;
using BoxForge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Configuration;

public static class CoreServiceRegistration
{
    public static IServiceCollection AddBoxForgeCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddBoxForgeOptions(configuration);

        services.AddSingleton<IClashParser, ClashParser>();
        services.AddSingleton<IConfigSerializer, ConfigSerializer>();
        services.AddSingleton<IProxyCacheIdGenerator, ProxyCacheIdGenerator>();
        services.AddSingleton<ISingboxConfigValidator, SingboxConfigValidator>();
        services.AddTransient<IProxyConverter, TrojanConverter>();
        services.AddTransient<IProxyConverter, VlessConverter>();
        services.AddTransient<IProxyConverter, Hysteria2Converter>();
        services.AddTransient<IProxyConverter, ShadowsocksConverter>();
        services.AddTransient<IProxyConverter, AnyTlsConverter>();

        services.AddTransient<NodeCatalogBuilder>();
        services.AddTransient<TailscaleEndpointBuilder>();
        services.AddTransient<DnsProfileBuilder>();
        services.AddTransient<RouteProfileBuilder>();

        services.AddTransient<ISingboxConfigBuilder, SingboxConfigBuilder>();
        services.AddTransient<ConversionService>();
        services.AddTransient<IBoxForgeEngine, BoxForgeEngine>();

        return services;
    }
}
