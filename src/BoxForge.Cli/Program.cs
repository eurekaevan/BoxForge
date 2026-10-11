using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using BoxForge.App;
using BoxForge.Configuration;

try
{
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
}
catch (OperationCanceledException)
{
    Environment.ExitCode = GenerateCommandRunner.CancelledExitCode;
}
catch (FormatException)
{
    Console.Error.WriteLine("BoxForge 启动失败：配置值格式无效，请检查 BOXFORGE_ 环境变量。");
    Environment.ExitCode = GenerateCommandRunner.FailureExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"BoxForge 启动失败（{exception.GetType().Name}）。");
    Environment.ExitCode = GenerateCommandRunner.FailureExitCode;
}
