using BoxForge.Configuration;
using BoxForge.Exceptions;
using BoxForge.Server.Api;
using Microsoft.AspNetCore.Http.Features;

namespace BoxForge.Server;

public sealed class Program
{
    public static void Main(string[] args)
    {
        try
        {
            WebApplication app = CreateApplication(args);
            app.Run();
        }
        catch (ProfileValidationException exception)
        {
            Console.Error.WriteLine($"BoxForge 启动失败：{exception.Message}");
            Environment.ExitCode = 1;
        }
    }

    public static WebApplication CreateApplication(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--Profile:", StringComparison.OrdinalIgnoreCase))
                throw new ProfileValidationException("profile.host", "Profile 只接受文件路径，不能包含子键。");
            if (args[index].Equals("--profile", StringComparison.OrdinalIgnoreCase)
                && (index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1])
                    || args[index + 1].StartsWith("--", StringComparison.Ordinal)))
                throw new ProfileValidationException("profile.host", "--profile 必须指定非空文件路径。");
        }
        IConfiguration arguments = new ConfigurationBuilder().AddCommandLine(args).Build();
        if (arguments.GetSection("Profile").GetChildren().Any())
            throw new ProfileValidationException("profile.host", "Profile 只接受文件路径，不能包含子键。");
        string? profilePath = arguments["Profile"];
        if (profilePath is not null && string.IsNullOrWhiteSpace(profilePath))
            throw new ProfileValidationException("profile.host", "--profile 必须指定非空文件路径。");

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        using var environment = new ConfigurationManager();
        environment.AddEnvironmentVariables(prefix: "BOXFORGE_");
        environment.ValidateBoxForgeEnvironment();
        if (profilePath != null)
            environment.AddInMemoryCollection(new Dictionary<string, string?> { ["Profile"] = profilePath });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = ApiLimits.MaxRequestBodyBytes;
        });
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = ApiLimits.MaxRequestBodyBytes;
            options.MemoryBufferThreshold = ApiLimits.MaxRequestBodyBytes;
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddBoxForgeCore(environment);

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.Use(SecurityHeaders.ApplyAsync);
        app.Use(ConversionApi.RejectOversizedRequestsAsync);
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapConversionApi();
        app.MapExportApi();
        return app;
    }
}
