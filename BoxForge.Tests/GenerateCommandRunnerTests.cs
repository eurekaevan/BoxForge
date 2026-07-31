using Microsoft.Extensions.Logging.Abstractions;
using BoxForge.App;
using BoxForge.Workflows;

namespace BoxForge.Tests;

public sealed class GenerateCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsInvalidArgumentsExitCode()
    {
        var workflow = new StubWorkflow(
            new LocalGenerationSummary(0, 0, 0));
        var runner = CreateRunner(workflow);

        int exitCode = await runner.RunAsync(
            ["generate", "--platform", "invalid"]);

        Assert.Equal(
            GenerateCommandRunner.InvalidArgumentsExitCode,
            exitCode);
        Assert.Equal(0, workflow.CallCount);
    }

    [Fact]
    public async Task RunAsync_WithoutCommand_DoesNotInvokeWorkflow()
    {
        var workflow = new StubWorkflow(
            new LocalGenerationSummary(0, 0, 0));
        var runner = CreateRunner(workflow);

        int exitCode = await runner.RunAsync([]);

        Assert.Equal(
            GenerateCommandRunner.InvalidArgumentsExitCode,
            exitCode);
        Assert.Equal(0, workflow.CallCount);
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

    [Fact]
    public async Task RunAsync_ReturnsCancelledWhenWorkflowIsCancelled()
    {
        var runner = CreateRunner(
            new ThrowingWorkflow(new OperationCanceledException()));

        int exitCode = await runner.RunAsync(["generate"]);

        Assert.Equal(GenerateCommandRunner.CancelledExitCode, exitCode);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureWhenWorkflowThrows()
    {
        var runner = CreateRunner(
            new ThrowingWorkflow(new IOException("write failed")));

        int exitCode = await runner.RunAsync(["generate"]);

        Assert.Equal(GenerateCommandRunner.FailureExitCode, exitCode);
    }

    private static GenerateCommandRunner CreateRunner(
        LocalGenerationSummary summary) =>
        CreateRunner(new StubWorkflow(summary));

    private static GenerateCommandRunner CreateRunner(
        ILocalGenerationWorkflow workflow) =>
        new(
            workflow,
            NullLogger<GenerateCommandRunner>.Instance);

    private sealed class StubWorkflow(
        LocalGenerationSummary summary) : ILocalGenerationWorkflow
    {
        public int CallCount { get; private set; }

        public Task<LocalGenerationSummary> GenerateAsync(
            LocalGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(summary);
        }
    }

    private sealed class ThrowingWorkflow(Exception exception)
        : ILocalGenerationWorkflow
    {
        public Task<LocalGenerationSummary> GenerateAsync(
            LocalGenerationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<LocalGenerationSummary>(exception);
    }
}
