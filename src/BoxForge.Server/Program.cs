using BoxForge.Configuration;
using BoxForge.Server.Api;
using Microsoft.AspNetCore.Http.Features;

namespace BoxForge.Server;

public sealed class Program
{
    public static void Main(string[] args)
    {
        WebApplication app = CreateApplication(args);
        app.Run();
    }

    public static WebApplication CreateApplication(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        using var environment = new ConfigurationManager();
        environment.AddEnvironmentVariables(prefix: "BOXFORGE_");
        environment.ValidateBoxForgeEnvironment();
        builder.Configuration.AddInMemoryCollection(environment.AsEnumerable().Where(pair => pair.Value is not null).ToArray());
        string? profilePath = new ConfigurationBuilder().AddCommandLine(args).Build()["Profile"];
        if (profilePath != null)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Profile"] = profilePath });
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
        builder.Services.AddBoxForgeCore(builder.Configuration);

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
