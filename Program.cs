using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using SubConvert.App;
using SubConvert.Builders;
using SubConvert.Builders.Components;
using SubConvert.Cli;
using SubConvert.Configuration;
using SubConvert.Converters;
using SubConvert.Infrastructure.FileSystem;
using SubConvert.Infrastructure.GitHub;
using SubConvert.Parsers;
using SubConvert.Services;
using SubConvert.Ui;
using SubConvert.Workflows;

bool generateMode = GenerateCommandParser.IsGenerateCommand(args);
string[] hostArguments = generateMode ? [] : args;

using var host = Host.CreateDefaultBuilder(hostArguments)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddEnvironmentVariables(prefix: "SUBCONVERT_");
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.ColorBehavior = Console.IsOutputRedirected
                ? LoggerColorBehavior.Disabled
                : LoggerColorBehavior.Enabled;
            options.SingleLine = true;
            options.TimestampFormat = "[HH:mm:ss] ";
        });
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSubConvertOptions(context.Configuration);
        services.AddTransient<IUserInterface, ConsoleUi>();

        services.AddSingleton<IClashParser, ClashParser>();
        services.AddSingleton<IConfigSerializer, ConfigSerializer>();
        services.AddSingleton<ISingboxConfigValidator, SingboxConfigValidator>();
        services.AddSingleton<IGitHubConfigRepositoryFactory, GitHubConfigRepositoryFactory>();
        services.AddSingleton<ILocalConfigDestination, LocalFileDestination>();

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
        services.AddTransient<ILocalGenerationWorkflow, LocalGenerationWorkflow>();

        services.AddTransient<ConversionOrchestrator>();
        services.AddTransient<GenerateCommandRunner>();
    })
    .Build();

if (generateMode)
{
    using var cancellationSource = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationSource.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        var command = host.Services.GetRequiredService<GenerateCommandRunner>();
        Environment.ExitCode = await command.RunAsync(
            args,
            cancellationSource.Token);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}
else
{
    var app = host.Services.GetRequiredService<ConversionOrchestrator>();
    await app.RunAsync();
}
