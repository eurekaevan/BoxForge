using BoxForge.Models;
using BoxForge.Models.Clash;
using BoxForge.Parsers;
using BoxForge.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

public sealed class LocalGenerationWorkflowTests
{
    [Fact]
    public async Task GenerateAsync_GeneratesAllFilesThenSkipsUnchangedBatch()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            TestInfrastructure.ValidShadowsocksYaml);
        await File.WriteAllTextAsync(
            Path.Combine(input, "beta.yml"),
            TestInfrastructure.ValidShadowsocksYaml);
        TargetPlatform[] platforms =
        [
            TargetPlatform.Android,
            TargetPlatform.Linux,
            TargetPlatform.Windows
        ];
        var request = new LocalGenerationRequest(input, output, platforms);
        using var services = TestInfrastructure.CreateServices();
        var workflow = TestInfrastructure.GetWorkflow(services);

        Assert.Equal(
            new LocalGenerationSummary(6, 0, 0),
            await workflow.GenerateAsync(request));
        Assert.Equal(
            new LocalGenerationSummary(0, 6, 0),
            await workflow.GenerateAsync(request));
        Assert.Equal(
            6,
            Directory.EnumerateFiles(
                output,
                "config.json",
                SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task GenerateAsync_ParsesEachYamlOnlyOnceForAllPlatforms()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            TestInfrastructure.ValidShadowsocksYaml);
        var parser = new CountingParser();
        using var services = TestInfrastructure.CreateServices(
            configure: registrations =>
                registrations.AddSingleton<IClashParser>(parser));

        LocalGenerationSummary summary = await TestInfrastructure
            .GetWorkflow(services)
            .GenerateAsync(new LocalGenerationRequest(
                input,
                temporary.GetPath("output"),
                [TargetPlatform.Android, TargetPlatform.Linux, TargetPlatform.Windows]));

        Assert.Equal(new LocalGenerationSummary(3, 0, 0), summary);
        Assert.Equal(1, parser.CallCount);
    }

    [Fact]
    public async Task GenerateAsync_RollsBackWholeBatchAndReportsDiscardedChanges()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.CreateDirectory("output");
        string marker = Path.Combine(output, "existing.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await File.WriteAllTextAsync(
            Path.Combine(input, "valid.yaml"),
            TestInfrastructure.ValidShadowsocksYaml);
        await File.WriteAllTextAsync(
            Path.Combine(input, "broken.yaml"),
            TestInfrastructure.InvalidHysteria2Yaml);

        using var services = TestInfrastructure.CreateServices();
        LocalGenerationSummary summary = await TestInfrastructure.GetWorkflow(services)
            .GenerateAsync(new LocalGenerationRequest(
                input,
                output,
                [TargetPlatform.Android]));

        Assert.Equal(new LocalGenerationSummary(0, 0, 1, 1), summary);
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        Assert.False(Directory.Exists(Path.Combine(output, "valid")));
    }

    [Fact]
    public async Task GenerateAsync_RejectsEmptyOrDuplicatePlatformsWithoutReplacingOutput()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.CreateDirectory("output");
        string marker = Path.Combine(output, "existing.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await File.WriteAllTextAsync(
            Path.Combine(input, "valid.yaml"),
            TestInfrastructure.ValidShadowsocksYaml);
        using var services = TestInfrastructure.CreateServices();
        var workflow = TestInfrastructure.GetWorkflow(services);

        Assert.True((await workflow.GenerateAsync(
            new LocalGenerationRequest(input, output, []))).HasFailures);
        Assert.True((await workflow.GenerateAsync(
            new LocalGenerationRequest(
                input,
                output,
                [TargetPlatform.Linux, TargetPlatform.Linux]))).HasFailures);
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task GenerateAsync_RejectsOutputParentOfInputThroughSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string realOutput = temporary.CreateDirectory("real-output");
        string input = Path.Combine(realOutput, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(
            Path.Combine(input, "valid.yaml"),
            TestInfrastructure.ValidShadowsocksYaml);
        string outputLink = temporary.GetPath("output-link");
        Directory.CreateSymbolicLink(outputLink, realOutput);

        using var services = TestInfrastructure.CreateServices();
        LocalGenerationSummary summary = await TestInfrastructure.GetWorkflow(services)
            .GenerateAsync(new LocalGenerationRequest(
                input,
                outputLink,
                [TargetPlatform.Linux]));

        Assert.True(summary.HasFailures);
        Assert.True(File.Exists(Path.Combine(input, "valid.yaml")));
    }

    [Fact]
    public async Task GenerateAsync_ReplacesStaleOutputOnlyAfterSuccess()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.CreateDirectory("output");
        await File.WriteAllTextAsync(Path.Combine(output, "stale.txt"), "remove");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            TestInfrastructure.ValidShadowsocksYaml);

        using var services = TestInfrastructure.CreateServices();
        LocalGenerationSummary summary = await TestInfrastructure.GetWorkflow(services)
            .GenerateAsync(new LocalGenerationRequest(
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

    private sealed class CountingParser : IClashParser
    {
        private readonly ClashParser inner = new();

        public int CallCount { get; private set; }

        public ClashConfig? Parse(string yamlContent)
        {
            CallCount++;
            return inner.Parse(yamlContent);
        }
    }
}
