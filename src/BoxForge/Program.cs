using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using BoxForge.App;
using BoxForge.Configuration;

using var host = Host.CreateDefaultBuilder([])
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddEnvironmentVariables(prefix: "BOXFORGE_");
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
        services.AddBoxForge(context.Configuration);
    })
    .Build();

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
