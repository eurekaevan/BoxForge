using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SubConvert.App;
using SubConvert.Builders;
using SubConvert.Builders.Components;
using SubConvert.Configuration;
using SubConvert.Converters;
using SubConvert.Infrastructure.FileSystem;
using SubConvert.Infrastructure.GitHub;
using SubConvert.Parsers;
using SubConvert.Services;
using SubConvert.Ui;
using SubConvert.Workflows;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddEnvironmentVariables(prefix: "SUBCONVERT_");
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSubConvertOptions(context.Configuration);
        services.AddTransient<IUserInterface, ConsoleUi>();

        services.AddSingleton<IClashParser, ClashParser>();
        services.AddSingleton<IConfigSerializer, ConfigSerializer>();
        services.AddSingleton<ISingboxConfigValidator, SingboxConfigValidator>();
        services.AddSingleton<IGitHubConfigRepositoryFactory, GitHubConfigRepositoryFactory>();
        services.AddSingleton<LocalFileDestination>();

        services.AddTransient<IProxyConverter, TrojanConverter>();
        services.AddTransient<IProxyConverter, VlessConverter>();
        services.AddTransient<IProxyConverter, Hysteria2Converter>();
        services.AddTransient<IProxyConverter, ShadowsocksConverter>();
        services.AddTransient<IProxyConverter, AnyTlsConverter>();

        services.AddTransient<NodeCatalogBuilder>();
        services.AddTransient<ProfilePlanner>();
        services.AddTransient<InboundBuilder>();
        services.AddTransient<TailscaleEndpointBuilder>();
        services.AddTransient<DnsProfileBuilder>();
        services.AddTransient<RouteProfileBuilder>();
        services.AddTransient<ExperimentalBuilder>();

        services.AddTransient<ISingboxConfigBuilder, SingboxConfigBuilder>();
        services.AddTransient<ConversionService>();
        services.AddTransient<ConversionWorkflow>();
        
        services.AddTransient<ConversionOrchestrator>();
    })
    .Build();

var app = host.Services.GetRequiredService<ConversionOrchestrator>();
await app.RunAsync();
