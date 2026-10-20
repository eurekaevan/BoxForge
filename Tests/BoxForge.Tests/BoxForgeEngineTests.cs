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
using BoxForge.Parsers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YamlDotNet.Core;

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

    [TestCase("metadata: &loop {next: *loop}")]
    [TestCase("metadata: &loop [*loop]")]
    [TestCase("metadata: &one {next: &two {next: *one}}")]
    public void RejectsCyclicYamlGraphsAsConversionFailures(string metadata)
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var error = Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
            await engine.ConvertAsync(new ConversionRequest("cycle", ValidYaml + "\n    " + metadata,
                [TargetPlatform.Android])));
        Assert.That(error!.InnerException, Is.TypeOf<NodeParseException>());
        Assert.That(error.InnerException!.Message, Does.Contain("循环引用"));
    }

    [Test]
    public async Task PreservesOrdinarySharedYamlAnchors()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        string yaml = ValidYaml + "\n    metadata: {first: &shared {note: example}, second: *shared}";
        ConversionBundle bundle = await engine.ConvertAsync(new ConversionRequest("shared", yaml,
            [TargetPlatform.Android]));
        Assert.That(bundle.Artifacts, Has.Count.EqualTo(1));
    }

    [Test]
    public void YamlExpansionBudgetAppliesAcrossAllProxyNodes()
    {
        var yaml = new StringBuilder(ValidYaml).Append("\n    metadata:\n      a0: &a0 [x]");
        for (int index = 1; index <= 14; index++)
            yaml.Append($"\n      a{index}: &a{index} [*a{index - 1}, *a{index - 1}]");
        var parser = new ClashParser();
        Assert.That(parser.Parse(yaml.ToString())!.Proxies, Has.Count.EqualTo(1));
        yaml.Append("\n  - name: second\n    metadata: *a14");
        var error = Assert.Throws<NodeParseException>(() => parser.Parse(yaml.ToString()));
        Assert.That(error!.Message, Does.Contain("100000"));
    }

    [Test]
    public void YamlExpansionBudgetAlsoCountsEmptyProxyMappings()
    {
        string yaml = "proxies:\n  - &empty {}" + string.Concat(Enumerable.Repeat("\n  - *empty", 100_000));
        var error = Assert.Throws<NodeParseException>(() => new ClashParser().Parse(yaml));
        Assert.That(error!.Message, Does.Contain("100000"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void YamlExpansionBudgetAlsoBoundsRepeatedText(bool longKey)
    {
        string text = new('x', 1024 * 1024);
        string shared = longKey ? $"{{'{text}': value}}" : $"{{key: '{text}'}}";
        var yaml = new StringBuilder(ValidYaml).Append("\n    metadata:\n      a0: &shared ").Append(shared);
        for (int index = 1; index <= 16; index++)
            yaml.Append($"\n      a{index}: *shared");
        var error = Assert.Throws<NodeParseException>(() => new ClashParser().Parse(yaml.ToString()));
        Assert.That(error!.Message, Does.Contain("UTF-8 字节"));
    }

    [Test]
    public void RejectsExcessiveYamlNestingWithoutTerminatingTheProcess()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        string yaml = ValidYaml + "\n    metadata: " + new string('[', 80) + "x" + new string(']', 80);
        Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
            await engine.ConvertAsync(new ConversionRequest("deep", yaml, [TargetPlatform.Android])));
    }

    [Test]
    public void StrictGenerationRejectsUnsupportedTransportBeforeBuildingArtifacts()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        const string yaml = """
            proxies:
              - name: unsafe-vless
                type: vless
                server: node.example.com
                port: 443
                uuid: 00000000-0000-4000-8000-000000000001
                network: xhttp
            """;

        BoxForgeConversionException? error = Assert.ThrowsAsync<BoxForgeConversionException>(
            async () => await engine.ConvertAsync(new ConversionRequest(
                "unsafe", yaml, [TargetPlatform.Android, TargetPlatform.Linux])));

        Assert.Multiple(() =>
        {
            Assert.That(error!.InnerException?.Message, Does.Contain("VLESS"));
            Assert.That(error.InnerException?.Message, Does.Contain("unsafe-vless"));
            Assert.That(error.InnerException?.Message, Does.Contain("network"));
        });
    }

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
        PreparedConversion prepared = conversionService.Prepare(ValidYaml);

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
    public void RejectsDuplicateYamlKeys()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        const string yaml = """
            proxies:
              - name: duplicate-key
                type: ss
                server: first.example.com
                server: second.example.com
                port: 443
                cipher: aes-128-gcm
                password: test-only
            """;

        BoxForgeConversionException? exception =
            Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
                await engine.ConvertAsync(new ConversionRequest(
                    "duplicate-key",
                    yaml,
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
                Is.TypeOf<ConfigValidationException>());
            Assert.That(
                recordingBuilder!.Calls,
                Is.EqualTo(new[]
                {
                    TargetPlatform.Android,
                    TargetPlatform.Linux
                }));
        });
    }

    [Test]
    public void PlanFailurePreservesStructuredDiagnosticsAndStopsPlatformGeneration()
    {
        RecordingConfigBuilder? recording = null;
        using ServiceProvider provider = CreateProvider(services =>
        {
            var profile = services.GetRequiredService<BoxForgeProfile>();
            return recording = new RecordingConfigBuilder(new SingboxConfigBuilder(
                new TailscaleEndpointBuilder(new BoxForgeProfile(BuiltInProfile.Default with
                { Runtime = profile.Runtime with { Tailscale = new(false, true) } })),
                services.GetRequiredService<DnsProfileBuilder>(), services.GetRequiredService<RouteProfileBuilder>(),
                profile));
        });
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        ConversionBundle? bundle = null;
        var error = Assert.ThrowsAsync<BoxForgePlatformConversionException>(async () =>
            bundle = await engine.ConvertAsync(new ConversionRequest("plan-failure", ValidYaml,
                [TargetPlatform.Android, TargetPlatform.Linux, TargetPlatform.Windows])))!;
        Assert.That(error.InnerException, Is.TypeOf<ConfigValidationException>());
        var diagnostics = ((ConfigValidationException)error.InnerException!).Diagnostics;
        Assert.Multiple(() =>
        {
            Assert.That(bundle, Is.Null);
            Assert.That(error.Platform, Is.EqualTo(TargetPlatform.Linux));
            Assert.That(recording!.Calls, Is.EqualTo(new[] { TargetPlatform.Android, TargetPlatform.Linux }));
            Assert.That(diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Path)), Is.EqualTo(new[]
            {
                ("IR004", "plan.dns.tailscale_enabled"),
                ("IR004", "plan.route.tailscale_enabled")
            }));
        });
    }

    [TestCase("proxies: null", "proxies")]
    [TestCase("proxies: [null]", "proxies[0]")]
    public void RejectsNullProxyStructuresWithNodeDiagnostics(string yaml, string location)
    {
        var error = Assert.Throws<NodeParseException>(() => new ClashParser().Parse(yaml));
        Assert.That(error!.Message, Does.Contain(location).And.Contain("null"));
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var wrapped = Assert.ThrowsAsync<BoxForgeConversionException>(() => engine.ConvertAsync(
            new ConversionRequest("invalid", yaml, [TargetPlatform.Android])));
        Assert.That(wrapped!.InnerException, Is.TypeOf<NodeParseException>());
    }

    [Test]
    public async Task ConcurrentParserInstancesKeepAliasesAndFailuresLocalToEachDocument()
    {
        ClashParser[] parsers = [new(), new(), new(), new()];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            ClashParser parser = parsers[worker % parsers.Length];
            for (int iteration = 0; iteration < 24; iteration++)
            {
                string marker = $"document-{worker}-{iteration}";
                string yaml = $$"""
                    proxies:
                      - name: {{marker}}
                        metadata:
                          first: &shared {marker: {{marker}}}
                          second: *shared
                    """;
                Assert.That(() => parser.Parse(yaml + "\n    name: duplicate"),
                    Throws.InstanceOf<YamlException>().With.Message.Contains("duplicate key"));
                Assert.That(() => parser.Parse("proxies:\n  - name: orphan\n    metadata: *shared"),
                    Throws.InstanceOf<YamlException>());

                var config = parser.Parse(yaml)!;
                var node = config.Proxies.Single();
                var metadata = node.GetObject("metadata")!;
                Assert.Multiple(() =>
                {
                    Assert.That(node.Name, Is.EqualTo(marker));
                    Assert.That(metadata.GetObject("first")!.GetString("marker"), Is.EqualTo(marker));
                    Assert.That(metadata.GetObject("second")!.GetString("marker"), Is.EqualTo(marker));
                });
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
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
            serviceProvider.GetRequiredService<BoxForgeProfile>());

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
                throw new ConfigValidationException([
                    new ConfigDiagnostic("TEST", "outbounds", $"模拟 {request.Platform} 校验失败。")]);
            }

            return inner.Build(request);
        }
    }
}
