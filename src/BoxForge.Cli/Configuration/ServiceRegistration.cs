using BoxForge.App;
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
        services.AddBoxForgeCore(configuration);
        services.AddTransient<ILocalGenerationWorkflow, LocalGenerationWorkflow>();
        services.AddTransient<GenerateCommandRunner>();

        return services;
    }
}
