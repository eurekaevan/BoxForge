using Microsoft.Extensions.Logging;
using BoxForge.Cli;
using BoxForge.Workflows;

namespace BoxForge.App;

public sealed class GenerateCommandRunner(
    ILocalGenerationWorkflow workflow,
    ILogger<GenerateCommandRunner> logger)
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int InvalidArgumentsExitCode = 2;
    public const int CancelledExitCode = 130;

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var parseResult = GenerateCommandParser.Parse(arguments);
        if (!parseResult.IsSuccess)
        {
            logger.LogError("{Error}", parseResult.Error);
            logger.LogError("{Usage}", GenerateCommandParser.Usage);
            return InvalidArgumentsExitCode;
        }

        try
        {
            var summary = await workflow.GenerateAsync(
                new LocalGenerationRequest(
                    parseResult.Options!.InputDirectory,
                    parseResult.Options.OutputDirectory,
                    parseResult.Options.Platforms),
                cancellationToken);
            logger.LogInformation(
                "■ 本地批量生成完成：✓ {Succeeded} 成功，↷ {Skipped} 跳过，✗ {Failed} 失败，共 {Total} 个。",
                summary.Succeeded,
                summary.Skipped,
                summary.Failed,
                summary.Total);

            return summary.Failed == 0
                ? SuccessExitCode
                : FailureExitCode;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("本地批量生成已取消。");
            return CancelledExitCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "本地批量生成失败：{Message}", ex.Message);
            logger.LogInformation(
                "■ 本地批量生成完成：✓ 0 成功，↷ 0 跳过，✗ 1 失败，共 1 个。");
            return FailureExitCode;
        }
    }
}
