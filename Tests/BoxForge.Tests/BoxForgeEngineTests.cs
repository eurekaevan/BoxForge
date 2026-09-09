using System.Security.Cryptography;
using System.Text;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BoxForge.Tests;

[TestFixture]
public sealed class BoxForgeEngineTests
{
    private const string ValidYaml = """
        proxies:
          - name: test
            type: ss
            server: node.example.com
            port: 443
            cipher: aes-128-gcm
            password: test-only
        """;

    [Test]
    public async Task ConvertsSinglePlatformInMemory()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionBundle bundle = await engine.ConvertAsync(
            new ConversionRequest(
                "single",
                ValidYaml,
                [TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(bundle.Name, Is.EqualTo("single"));
            Assert.That(bundle.Artifacts, Has.Count.EqualTo(1));
            Assert.That(
                bundle.Artifacts[0].Platform,
                Is.EqualTo(TargetPlatform.Android));
            Assert.That(bundle.Artifacts[0].Content, Does.Contain("\"outbounds\""));
        });
    }

    [Test]
    public async Task ConvertsEachOfThreePlatformsExactlyOnce()
    {
        RecordingConfigBuilder? recordingBuilder = null;
        using ServiceProvider provider = CreateProvider(serviceProvider =>
            recordingBuilder = new RecordingConfigBuilder(
                CreateConfigBuilder(serviceProvider)));
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionBundle bundle = await engine.ConvertAsync(
            new ConversionRequest(
                "all",
                ValidYaml,
                [
                    TargetPlatform.Android,
                    TargetPlatform.Linux,
                    TargetPlatform.Windows
                ]));

        Assert.Multiple(() =>
        {
            Assert.That(bundle.Artifacts, Has.Count.EqualTo(3));
            Assert.That(
                recordingBuilder!.Calls,
                Is.EqualTo(new[]
                {
                    TargetPlatform.Android,
                    TargetPlatform.Linux,
                    TargetPlatform.Windows
                }));
        });
    }

    [Test]
    public async Task UsesDeterministicPlatformOrder()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionBundle bundle = await engine.ConvertAsync(
            new ConversionRequest(
                "ordered",
                ValidYaml,
                [
                    TargetPlatform.Windows,
                    TargetPlatform.Android,
                    TargetPlatform.Linux
                ]));

        Assert.That(
            bundle.Artifacts.Select(artifact => artifact.Platform),
            Is.EqualTo(new[]
            {
                TargetPlatform.Android,
                TargetPlatform.Linux,
                TargetPlatform.Windows
            }));
    }

    [Test]
    public async Task ComputesLowercaseSha256FromUtf8Content()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionArtifact artifact = (await engine.ConvertAsync(
            new ConversionRequest(
                "hash",
                ValidYaml,
                [TargetPlatform.Linux])))
            .Artifacts.Single();
        string expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(artifact.Content)));

        Assert.Multiple(() =>
        {
            Assert.That(artifact.Sha256, Is.EqualTo(expectedHash));
            Assert.That(artifact.Sha256, Does.Match("^[0-9a-f]{64}$"));
        });
    }

    [Test]
    public async Task MatchesExistingConversionServiceOutputByteForByte()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var conversionService = provider.GetRequiredService<ConversionService>();
        PreparedConversion prepared = conversionService.Prepare(
            ValidYaml,
            strictNodeValidation: true);

        ConversionBundle bundle = await engine.ConvertAsync(
            new ConversionRequest(
                "compatibility",
                ValidYaml,
                [
                    TargetPlatform.Windows,
                    TargetPlatform.Android,
                    TargetPlatform.Linux
                ]));

        Assert.That(
            bundle.Artifacts.Select(artifact => artifact.Content),
            Is.EqualTo(bundle.Artifacts.Select(artifact =>
                conversionService.Convert(prepared, artifact.Platform))));
    }

    [Test]
    public void RejectsEmptyName()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                " ",
                ValidYaml,
                [TargetPlatform.Android])));
    }

    [Test]
    public void RejectsEmptyYaml()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                "empty-yaml",
                " ",
                [TargetPlatform.Android])));
    }

    [Test]
    public void RejectsEmptyPlatformList()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest("empty-platforms", ValidYaml, [])));
    }

    [Test]
    public void RejectsDuplicatePlatforms()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                "duplicates",
                ValidYaml,
                [TargetPlatform.Android, TargetPlatform.Android])));
    }

    [Test]
    public void RejectsUndefinedPlatform()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                "undefined",
                ValidYaml,
                [(TargetPlatform)999])));
    }

    [Test]
    public void WrapsInvalidYamlInStableConversionException()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        BoxForgeConversionException? exception =
            Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
                await engine.ConvertAsync(new ConversionRequest(
                "invalid-yaml",
                "proxies:\n  - name: [",
                [TargetPlatform.Android])));

        Assert.That(exception!.InnerException, Is.Not.Null);
    }

    [Test]
    public void PlatformFailureDoesNotReturnPartialBundle()
    {
        RecordingConfigBuilder? recordingBuilder = null;
        using ServiceProvider provider = CreateProvider(serviceProvider =>
            recordingBuilder = new RecordingConfigBuilder(
                CreateConfigBuilder(serviceProvider),
                TargetPlatform.Linux));
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        ConversionBundle? bundle = null;

        BoxForgePlatformConversionException? exception =
            Assert.ThrowsAsync<BoxForgePlatformConversionException>(async () =>
            bundle = await engine.ConvertAsync(new ConversionRequest(
                "failure",
                ValidYaml,
                [
                    TargetPlatform.Android,
                    TargetPlatform.Linux,
                    TargetPlatform.Windows
                ])));

        Assert.Multiple(() =>
        {
            Assert.That(bundle, Is.Null);
            Assert.That(
                exception!.Platform,
                Is.EqualTo(TargetPlatform.Linux));
            Assert.That(
                exception.InnerException,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(
                recordingBuilder!.Calls,
                Is.EqualTo(new[]
                {
                    TargetPlatform.Android,
                    TargetPlatform.Linux
                }));
        });
    }

    private static ServiceProvider CreateProvider(
        Func<IServiceProvider, ISingboxConfigBuilder>? configBuilderFactory = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBoxForgeCore(configuration);

        if (configBuilderFactory != null)
        {
            services.RemoveAll<ISingboxConfigBuilder>();
            services.AddSingleton(configBuilderFactory);
        }

        return services.BuildServiceProvider();
    }

    private static ISingboxConfigBuilder CreateConfigBuilder(
        IServiceProvider serviceProvider) =>
        new SingboxConfigBuilder(
            serviceProvider.GetRequiredService<TailscaleEndpointBuilder>(),
            serviceProvider.GetRequiredService<DnsProfileBuilder>(),
            serviceProvider.GetRequiredService<RouteProfileBuilder>(),
            serviceProvider.GetRequiredService<SingboxApiServiceBuilder>());

    private sealed class RecordingConfigBuilder(
        ISingboxConfigBuilder inner,
        TargetPlatform? failingPlatform = null) : ISingboxConfigBuilder
    {
        public List<TargetPlatform> Calls { get; } = [];

        public SingboxConfig Build(SingboxBuildRequest request)
        {
            Calls.Add(request.Platform);
            if (request.Platform == failingPlatform)
            {
                throw new InvalidOperationException(
                    $"模拟 {request.Platform} 转换失败。");
            }

            return inner.Build(request);
        }
    }
}
