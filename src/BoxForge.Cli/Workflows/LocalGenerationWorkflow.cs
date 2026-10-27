using Microsoft.Extensions.Logging;
using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;

namespace BoxForge.Workflows;

public sealed partial class LocalGenerationWorkflow(
    IBoxForgeEngine engine,
    ILogger<LocalGenerationWorkflow> logger) : ILocalGenerationWorkflow
{
    public async Task<LocalGenerationSummary> GenerateAsync(
        LocalGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.InputDirectory)
            || string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            LogEmptyDirectories(logger);
            return new LocalGenerationSummary(0, 0, 1);
        }

        if (request.Platforms.Count == 0
            || request.Platforms.Any(platform => !Enum.IsDefined(platform))
            || request.Platforms.Distinct().Count() != request.Platforms.Count)
        {
            LogInvalidPlatforms(logger);
            return new LocalGenerationSummary(0, 0, 1);
        }

        InputDiscovery? input = InputDiscovery.Find(request.InputDirectory, request.OutputDirectory, logger);
        if (input is null)
        {
            return new LocalGenerationSummary(0, 0, 1);
        }

        using var output = new OutputTransaction(input.OutputDirectory, logger);
        var changedItems = new List<(string ConfigName, TargetPlatform Platform)>();
        int skipped = 0;
        int failed = 0;

        foreach (string inputFile in input.Files)
        {
            string? configName = input.GetConfigurationName(inputFile);
            if (configName is null)
            {
                failed += request.Platforms.Count;
                continue;
            }

            string yamlContent;
            try
            {
                yamlContent = await File.ReadAllTextAsync(
                    inputFile,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogReadFailure(logger, ex, inputFile);
                failed += request.Platforms.Count;
                continue;
            }

            ConversionBundle bundle;
            try
            {
                bundle = await engine.ConvertAsync(
                    new ConversionRequest(
                        configName,
                        yamlContent,
                        request.Platforms),
                    cancellationToken);
            }
            catch (BoxForgePlatformConversionException ex)
            {
                failed += request.Platforms.Count;
                LogGenerationFailure(
                    logger,
                    ex.InnerException ?? ex,
                    configName,
                    ex.Platform);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed += request.Platforms.Count;
                LogPreparationFailure(logger, ex, configName);
                continue;
            }

            foreach (ConversionArtifact artifact in bundle.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TargetPlatform platform = artifact.Platform;
                try
                {
                    if (await output.StageAsync(configName, artifact, cancellationToken))
                    {
                        changedItems.Add((configName, platform));
                    }
                    else
                    {
                        skipped++;
                        LogUnchanged(logger, configName, platform);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    LogGenerationFailure(logger, ex, configName, platform);
                }
            }
        }

        if (failed > 0)
        {
            LogDiscardedBatch(logger, changedItems.Count);
            return new LocalGenerationSummary(0, skipped, failed, changedItems.Count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await output.CommitAsync(cancellationToken);
        foreach (var item in changedItems)
        {
            LogGenerated(logger, item.ConfigName, item.Platform);
        }

        return new LocalGenerationSummary(changedItems.Count, skipped, 0);
    }

    [LoggerMessage(1, LogLevel.Error, "输入目录和输出目录不能为空。")]
    private static partial void LogEmptyDirectories(ILogger logger);

    [LoggerMessage(2, LogLevel.Error, "目标平台必须非空、有效且不重复。")]
    private static partial void LogInvalidPlatforms(ILogger logger);

    [LoggerMessage(10, LogLevel.Error, "读取配置失败：{InputFile}")]
    private static partial void LogReadFailure(ILogger logger, Exception exception, string inputFile);

    [LoggerMessage(11, LogLevel.Error, "✗ 配置预处理失败：{ConfigName}")]
    private static partial void LogPreparationFailure(ILogger logger, Exception exception, string configName);

    [LoggerMessage(12, LogLevel.Information, "↷ 未变化：{ConfigName} [{Platform}]")]
    private static partial void LogUnchanged(ILogger logger, string configName, TargetPlatform platform);

    [LoggerMessage(13, LogLevel.Error, "✗ 生成失败：{ConfigName} [{Platform}]")]
    private static partial void LogGenerationFailure(ILogger logger, Exception exception, string configName, TargetPlatform platform);

    [LoggerMessage(14, LogLevel.Warning, "存在生成失败项，保留原输出目录不变，丢弃 {Discarded} 个已暂存更改。")]
    private static partial void LogDiscardedBatch(ILogger logger, int discarded);

    [LoggerMessage(15, LogLevel.Information, "✓ 已生成：{ConfigName} [{Platform}]")]
    private static partial void LogGenerated(ILogger logger, string configName, TargetPlatform platform);
}
