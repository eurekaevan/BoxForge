using Microsoft.Extensions.Logging.Abstractions;
using SubConvert.App;
using SubConvert.Cli;
using SubConvert.Workflows;

namespace SubConvert.Tests;

public sealed class GenerateCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsInvalidArgumentsExitCode()
    {
        var runner = CreateRunner(new LocalGenerationSummary(0, 0, 0));

        int exitCode = await runner.RunAsync(
            ["generate", "--platform", "invalid"]);

        Assert.Equal(
            GenerateCommandRunner.InvalidArgumentsExitCode,
            exitCode);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureWhenAnyItemFailed()
    {
        var runner = CreateRunner(new LocalGenerationSummary(2, 1, 1));

        int exitCode = await runner.RunAsync(["generate"]);

        Assert.Equal(GenerateCommandRunner.FailureExitCode, exitCode);
    }

    [Fact]
    public async Task RunAsync_ReturnsSuccessWithoutFailures()
    {
        var runner = CreateRunner(new LocalGenerationSummary(2, 1, 0));

        int exitCode = await runner.RunAsync(["generate"]);

        Assert.Equal(GenerateCommandRunner.SuccessExitCode, exitCode);
    }

    private static GenerateCommandRunner CreateRunner(
        LocalGenerationSummary summary) =>
        new(
            new StubWorkflow(summary),
            NullLogger<GenerateCommandRunner>.Instance);

    private sealed class StubWorkflow(
        LocalGenerationSummary summary) : ILocalGenerationWorkflow
    {
        public Task<LocalGenerationSummary> GenerateAsync(
            GenerateCommandOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(summary);
    }
}
