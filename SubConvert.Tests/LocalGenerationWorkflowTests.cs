using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubConvert.Builders;
using SubConvert.Builders.Components;
using SubConvert.Cli;
using SubConvert.Configuration;
using SubConvert.Converters;
using SubConvert.Models;
using SubConvert.Parsers;
using SubConvert.Services;
using SubConvert.Workflows;

namespace SubConvert.Tests;

public sealed class LocalGenerationWorkflowTests
{
    private const string ValidShadowsocksYaml = """
        proxies:
          - name: test-node
            type: ss
            server: 127.0.0.1
            port: 8388
            cipher: aes-128-gcm
            password: secret
        """;

    private const string InvalidHysteriaYaml = """
        proxies:
          - name: invalid-node
            type: hysteria2
            server: example.com
            ports: nope-range
            password: secret
        """;

    private const string UnsupportedNodeYaml = """
        proxies:
          - name: unsupported-node
            type: vmess
            server: example.com
            port: 443
            uuid: 11111111-1111-1111-1111-111111111111
        """;

    [Fact]
    public async Task GenerateAsync_GeneratesYamlAndYmlForAllPlatforms()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            ValidShadowsocksYaml);
        await File.WriteAllTextAsync(
            Path.Combine(input, "beta.yml"),
            ValidShadowsocksYaml);

        var workflow = CreateWorkflow();
        var options = new GenerateCommandOptions(
            input,
            output,
            [
                TargetPlatform.Android,
                TargetPlatform.Linux,
                TargetPlatform.Windows
            ]);

        var firstRun = await workflow.GenerateAsync(options);

        Assert.Equal(new LocalGenerationSummary(6, 0, 0), firstRun);
        foreach (string configName in new[] { "alpha", "beta" })
        {
            foreach (TargetPlatform platform in options.Platforms)
            {
                Assert.True(File.Exists(Path.Combine(
                    output,
                    configName,
                    platform.ToString(),
                    "config.json")));
            }
        }

        var contentsBefore = Directory
            .EnumerateFiles(output, "config.json", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(output, path),
                File.ReadAllText,
                StringComparer.Ordinal);

        var secondRun = await workflow.GenerateAsync(options);

        Assert.Equal(new LocalGenerationSummary(0, 6, 0), secondRun);
        var contentsAfter = Directory
            .EnumerateFiles(output, "config.json", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(output, path),
                File.ReadAllText,
                StringComparer.Ordinal);
        Assert.Equal(contentsBefore, contentsAfter);
    }

    [Fact]
    public async Task GenerateAsync_PreservesExistingOutputWhenConversionFails()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.CreateDirectory("output");
        string marker = Path.Combine(output, "existing.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await File.WriteAllTextAsync(
            Path.Combine(input, "valid.yaml"),
            ValidShadowsocksYaml);
        await File.WriteAllTextAsync(
            Path.Combine(input, "broken.yaml"),
            InvalidHysteriaYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new GenerateCommandOptions(
                input,
                output,
                [TargetPlatform.Android]));

        Assert.Equal(1, summary.Failed);
        Assert.True(File.Exists(marker));
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        Assert.False(Directory.Exists(Path.Combine(output, "valid")));
    }

    [Fact]
    public async Task GenerateAsync_FailsForUnsupportedNodesInStrictMode()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "unsupported.yaml"),
            UnsupportedNodeYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new GenerateCommandOptions(
                input,
                output,
                [TargetPlatform.Windows]));

        Assert.Equal(new LocalGenerationSummary(0, 0, 1), summary);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerateAsync_ReplacesStaleOutputAfterCompleteSuccess()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.CreateDirectory("output");
        await File.WriteAllTextAsync(
            Path.Combine(output, "stale.txt"),
            "remove");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            ValidShadowsocksYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new GenerateCommandOptions(
                input,
                output,
                [TargetPlatform.Linux]));

        Assert.Equal(new LocalGenerationSummary(1, 0, 0), summary);
        Assert.False(File.Exists(Path.Combine(output, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "alpha",
            "Linux",
            "config.json")));
    }

    [Fact]
    public async Task GenerateAsync_RejectsOutputDirectoryContainingInput()
    {
        using var temporary = new TemporaryDirectory();
        string output = temporary.CreateDirectory("output");
        string input = Path.Combine(output, "input");
        Directory.CreateDirectory(input);
        string inputFile = Path.Combine(input, "alpha.yaml");
        await File.WriteAllTextAsync(inputFile, ValidShadowsocksYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new GenerateCommandOptions(
                input,
                output,
                [TargetPlatform.Android]));

        Assert.Equal(new LocalGenerationSummary(0, 0, 1), summary);
        Assert.True(File.Exists(inputFile));
    }

    private static LocalGenerationWorkflow CreateWorkflow()
    {
        var singboxOptions = Options.Create(new SingboxOptions());
        var tailscaleOptions = Options.Create(new TailscaleOptions());
        IProxyConverter[] converters =
        [
            new TrojanConverter(),
            new VlessConverter(),
            new Hysteria2Converter(),
            new ShadowsocksConverter(),
            new AnyTlsConverter()
        ];
        var nodeCatalogBuilder = new NodeCatalogBuilder(
            converters,
            NullLogger<NodeCatalogBuilder>.Instance);
        var configBuilder = new SingboxConfigBuilder(
            nodeCatalogBuilder,
            new ProfilePlanner(singboxOptions),
            new InboundBuilder(),
            new TailscaleEndpointBuilder(tailscaleOptions),
            new DnsProfileBuilder(singboxOptions, tailscaleOptions),
            new RouteProfileBuilder(singboxOptions, tailscaleOptions),
            new ExperimentalBuilder());
        var serializer = new ConfigSerializer();
        var conversionService = new ConversionService(
            new ClashParser(),
            configBuilder,
            new SingboxConfigValidator(),
            serializer);

        return new LocalGenerationWorkflow(
            conversionService,
            NullLogger<LocalGenerationWorkflow>.Instance);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            $"SubConvert.Tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string GetPath(string relativePath) =>
            Path.Combine(root, relativePath);

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
}
