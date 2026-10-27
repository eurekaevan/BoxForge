using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using BoxForge.App;
using BoxForge.Exceptions;
using BoxForge.Configuration;
using BoxForge.Cli;
using BoxForge.Workflows;

try
{
    var parsed = GenerateCommandParser.Parse(args);
    if (!parsed.IsSuccess)
    {
        Console.Error.WriteLine(parsed.Error);
        Console.Error.WriteLine(GenerateCommandParser.Usage);
        Environment.ExitCode = GenerateCommandRunner.InvalidArgumentsExitCode;
        return;
    }

    using var host = Host.CreateDefaultBuilder([])
        .ConfigureServices((_, services) =>
        {
            using var environment = new ConfigurationManager();
            environment.AddEnvironmentVariables(prefix: "BOXFORGE_");
            environment.ValidateBoxForgeEnvironment();
            if (parsed.Options!.ProfilePath is { } path)
                environment.AddInMemoryCollection(new Dictionary<string, string?> { ["Profile"] = path });
            services.AddBoxForge(environment);
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
        .Build();

    if (parsed.Options!.ShowResolved)
    {
        Console.WriteLine(host.Services.GetRequiredService<BoxForgeProfile>().ToResolvedJson());
        return;
    }

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
            new LocalGenerationRequest(
                parsed.Options.InputDirectory,
                parsed.Options.OutputDirectory,
                parsed.Options.Platforms),
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
catch (ProfileValidationException exception)
{
    Console.Error.WriteLine($"BoxForge 启动失败：配置值格式无效，请检查 profile 文件或 BOXFORGE_ 环境变量。{exception.Message}");
    Environment.ExitCode = GenerateCommandRunner.FailureExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"BoxForge 启动失败（{exception.GetType().Name}）。");
    Environment.ExitCode = GenerateCommandRunner.FailureExitCode;
}
