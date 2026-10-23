using BoxForge.Exceptions;
using BoxForge.Builders;
using BoxForge.Engine;
using BoxForge.Parsers;
using BoxForge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Configuration;

public static class CoreServiceRegistration
{
    /// <summary>Validate the roots of a source loaded with BOXFORGE_ before merging it into host configuration.</summary>
    public static void ValidateBoxForgeEnvironment(this IConfiguration environment)
    {
        foreach (var section in environment.GetChildren())
        {
            if (section.Key.Equals("Profile", StringComparison.OrdinalIgnoreCase))
            {
                if (section.GetChildren().Any())
                    throw new ProfileValidationException("profile.host", "BOXFORGE_Profile 只接受文件路径，不能包含子键。");
            }
            else if (!ProfileConfiguration.IsProfileSection(section.Key))
                throw new ProfileValidationException("profile.host", "BOXFORGE_ 环境变量必须映射到 profile 键。");
        }
    }

    public static IServiceCollection AddBoxForgeCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(ProfileConfiguration.ResolveProfile(configuration));

        services.AddSingleton<IClashParser, ClashParser>();
        services.AddSingleton<IConfigSerializer, ConfigSerializer>();
        services.AddSingleton<IProxyCacheIdGenerator, ProxyCacheIdGenerator>();
        services.AddSingleton<ISingboxConfigValidator, SingboxConfigValidator>();

        services.AddTransient<ISingboxConfigBuilder, SingboxConfigBuilder>();
        services.AddTransient<IBoxForgeEngine, BoxForgeEngine>();

        return services;
    }
}
