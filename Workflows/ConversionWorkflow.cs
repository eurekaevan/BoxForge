using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubConvert.Configuration;
using SubConvert.Models;
using SubConvert.Services;

namespace SubConvert.Workflows;

public class ConversionWorkflow(
    ConversionService conversionService,
    IOptions<OutputOptions> outputOptions,
    ILogger<ConversionWorkflow> logger)
{
    private readonly OutputOptions _outputOptions = outputOptions.Value;

    public async Task ProcessBatchAsync(
        IConfigSource source,
        IConfigDestination destination,
        IReadOnlyList<ConfigSourceItem> items,
        TargetPlatform platform)
    {
        logger.LogInformation("● 开始批量处理，共 {Count} 个配置。", items.Count);
        int success = 0, failed = 0;

        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            logger.LogInformation(
                "▶ [{Current}/{Total}] {DisplayName}",
                index + 1,
                items.Count,
                item.DisplayName);
            try
            {
                var result = await ConvertAsync(source, item, platform);
                
                string targetPath = $"{_outputOptions.BaseFolder}/{item.DisplayName}/{platform}/config.json";
                string changeDescription =
                    $"{item.DisplayName} sing-box config [{platform}]";

                await destination.WriteAsync(new ConfigWriteRequest(
                    targetPath,
                    result.JsonContent,
                    changeDescription));
                
                logger.LogInformation(
                    "✓ 写入成功：{DisplayName} → {TargetPath}",
                    item.DisplayName,
                    targetPath);
                success++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "✗ {DisplayName} 处理失败：{Message}", item.DisplayName, ex.Message);
                failed++;
            }
        }

        logger.LogInformation(
            "■ 批量处理完成：✓ {Success} 成功，✗ {Failed} 失败，共 {Total} 个。",
            success,
            failed,
            items.Count);
    }

    public async Task ProcessSingleAsync(
        IConfigSource source,
        IConfigDestination destination,
        ConfigSourceItem item,
        TargetPlatform platform)
    {
        logger.LogInformation("▶ 正在处理 {DisplayName}...", item.DisplayName);
        try
        {
            var result = await ConvertAsync(source, item, platform);

            await destination.WriteAsync(new ConfigWriteRequest(
                _outputOptions.LocalFile,
                result.JsonContent));
            logger.LogInformation(
                "✓ 生成成功：{DisplayName} ({Platform}) → {LocalOutputFile}",
                item.DisplayName,
                platform,
                _outputOptions.LocalFile);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "✗ 处理失败：{Message}", ex.Message);
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
