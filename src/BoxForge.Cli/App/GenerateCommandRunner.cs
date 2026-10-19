using Microsoft.Extensions.Logging;
using BoxForge.Workflows;

namespace BoxForge.App;

public sealed partial class GenerateCommandRunner(
    ILocalGenerationWorkflow workflow,
    ILogger<GenerateCommandRunner> logger)
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int InvalidArgumentsExitCode = 2;
    public const int CancelledExitCode = 130;

    public async Task<int> RunAsync(
        LocalGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = await workflow.GenerateAsync(
                request,
                cancellationToken);
            LogSummary(
                logger,
                summary.Succeeded,
                summary.Skipped,
                summary.Failed,
                summary.Discarded,
                summary.Total);

            return summary.HasFailures ? FailureExitCode : SuccessExitCode;
        }
        catch (OperationCanceledException)
        {
            LogCancelled(logger);
            return CancelledExitCode;
        }
        catch (Exception ex)
        {
            LogUnhandledFailure(logger, ex);
            return FailureExitCode;
        }
    }

    [LoggerMessage(3, LogLevel.Information, "■ 本地批量生成完成：✓ {Succeeded} 成功，↷ {Skipped} 跳过，✗ {Failed} 失败，⊘ {Discarded} 已回滚，共 {Total} 个。")]
    private static partial void LogSummary(ILogger logger, int succeeded, int skipped, int failed, int discarded, int total);

    [LoggerMessage(4, LogLevel.Warning, "本地批量生成已取消。")]
    private static partial void LogCancelled(ILogger logger);

    [LoggerMessage(5, LogLevel.Error, "本地批量生成失败。")]
    private static partial void LogUnhandledFailure(ILogger logger, Exception exception);
}
