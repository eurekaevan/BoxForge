using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubConvert.Configuration;
using SubConvert.Models;
using SubConvert.Services;

namespace SubConvert.Workflows;

public class ConversionWorkflow(
    ConversionService conversionService,
    IOptions<GitHubOptions> githubOptions,
    IOptions<OutputOptions> outputOptions,
    ILogger<ConversionWorkflow> logger)
{
    private readonly GitHubOptions _githubOptions = githubOptions.Value;
    private readonly OutputOptions _outputOptions = outputOptions.Value;

    public async Task ProcessBatchAsync(
        IConfigSource source,
        IConfigDestination destination,
        IReadOnlyList<ConfigSourceItem> items,
        TargetPlatform platform,
        string owner)
    {
        logger.LogInformation("开始批量处理 {Count} 个机场配置...", items.Count);
        int success = 0, failed = 0;

        foreach (var item in items)
        {
            logger.LogInformation("──────────────────────────────────────");
            logger.LogInformation("处理中：{DisplayName}", item.DisplayName);
            try
            {
                var result = await ConvertAsync(source, item, platform);
                
                string targetPath = $"{_outputOptions.BaseFolder}/{item.DisplayName}/{platform}/config.json";
                string commitMessage = $"chore: update {item.DisplayName} sing-box config [{platform}]";

                logger.LogInformation("正在上传到 {Repo}/{TargetPath}...", _githubOptions.Repository, targetPath);
                await destination.WriteAsync(
                    targetPath,
                    result.JsonContent,
                    commitMessage);
                
                logger.LogInformation("上传成功: {DisplayName} -> {Owner}/{Repo}/{TargetPath}", item.DisplayName, owner, _githubOptions.Repository, targetPath);
                success++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{DisplayName} 处理失败：{Message}", item.DisplayName, ex.Message);
                failed++;
            }
        }

        logger.LogInformation("══════════════════════════════════════");
        logger.LogInformation("批量处理完成：{Success} 成功，{Failed} 失败。", success, failed);
    }

    public async Task ProcessSingleAsync(
        IConfigSource source,
        IConfigDestination destination,
        ConfigSourceItem item,
        TargetPlatform platform)
    {
        logger.LogInformation("正在下载 {DisplayName} 配置...", item.DisplayName);
        try
        {
            var result = await ConvertAsync(source, item, platform);

            await destination.WriteAsync(
                _outputOptions.LocalFile,
                result.JsonContent);
            logger.LogInformation("生成成功: {DisplayName} ({Platform}) -> {LocalOutputFile}", item.DisplayName, platform, _outputOptions.LocalFile);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理失败：{Message}", ex.Message);
        }
    }

    private async Task<ConversionResult> ConvertAsync(
        IConfigSource source,
        ConfigSourceItem item,
        TargetPlatform platform)
    {
        string yamlContent = await source.ReadAsync(item.Path);
        return conversionService.Convert(yamlContent, platform);
    }
}
