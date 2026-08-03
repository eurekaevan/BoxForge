using BoxForge.Configuration;
using BoxForge.Services;
using BoxForge.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

internal static class TestInfrastructure
{
    public const string ValidShadowsocksYaml = """
        proxies:
          - name: test-node
            type: ss
            server: 127.0.0.1
            port: 8388
            cipher: aes-128-gcm
            password: secret
        """;

    public const string ValidHysteria2Yaml = """
        proxies:
          - name: test-hysteria2
            type: hysteria2
            server: example.com
            ports: 20000-30000
            password: secret
            sni: example.com
        """;

    public const string InvalidHysteria2Yaml = """
        proxies:
          - name: invalid-node
            type: hysteria2
            server: example.com
            ports: nope-range
            password: secret
        """;

    public static ServiceProvider CreateServices(
        IReadOnlyDictionary<string, string?>? values = null,
        Action<IServiceCollection>? configure = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBoxForge(configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    public static LocalGenerationWorkflow GetWorkflow(
        ServiceProvider services) =>
        (LocalGenerationWorkflow)services
            .GetRequiredService<ILocalGenerationWorkflow>();

    public static string Convert(
        string yaml,
        Models.TargetPlatform platform,
        IReadOnlyDictionary<string, string?>? values = null)
    {
        using var services = CreateServices(values);
        var converter = services.GetRequiredService<ConversionService>();
        return converter.Convert(
            converter.Prepare(yaml, strictNodeValidation: true),
            platform);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"BoxForge.Tests-{Guid.NewGuid():N}");

    public TemporaryDirectory() => Directory.CreateDirectory(root);

    public string GetPath(string relativePath) => Path.Combine(root, relativePath);

    public string CreateDirectory(string relativePath)
    {
        string path = GetPath(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
