using Microsoft.Extensions.Logging;
using BoxForge.Cli;
using BoxForge.Services;

namespace BoxForge.Workflows;

public sealed record LocalGenerationSummary(
    int Succeeded,
    int Skipped,
    int Failed)
{
    public int Total => Succeeded + Skipped + Failed;
}

public interface ILocalGenerationWorkflow
{
    Task<LocalGenerationSummary> GenerateAsync(
        GenerateCommandOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class LocalGenerationWorkflow(
    ConversionService conversionService,
    ILogger<LocalGenerationWorkflow> logger) : ILocalGenerationWorkflow
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml"];

    public async Task<LocalGenerationSummary> GenerateAsync(
        GenerateCommandOptions options,
        CancellationToken cancellationToken = default)
    {
        string inputDirectory = Path.GetFullPath(options.InputDirectory);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        if (!Directory.Exists(inputDirectory))
        {
            logger.LogError("输入目录不存在：{InputDirectory}", inputDirectory);
            return new LocalGenerationSummary(0, 0, 1);
        }

        if (string.Equals(
            inputDirectory.TrimEnd(Path.DirectorySeparatorChar),
            outputDirectory.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("输入目录与输出目录不能相同。");
            return new LocalGenerationSummary(0, 0, 1);
        }

        if (IsSameOrChildPath(inputDirectory, outputDirectory))
        {
            logger.LogError(
                "输出目录不能是输入目录的父目录，否则替换输出时会删除输入。");
            return new LocalGenerationSummary(0, 0, 1);
        }

        if (File.Exists(outputDirectory))
        {
            logger.LogError(
                "输出路径已存在且不是目录：{OutputDirectory}",
                outputDirectory);
            return new LocalGenerationSummary(0, 0, 1);
        }

        string[] inputFiles = [.. Directory
            .EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedInput)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];
        if (inputFiles.Length == 0)
        {
            logger.LogError(
                "输入目录内没有找到 .yaml 或 .yml 文件：{InputDirectory}",
                inputDirectory);
            return new LocalGenerationSummary(0, 0, 1);
        }

        string outputParent = Directory.GetParent(outputDirectory)?.FullName
            ?? throw new InvalidOperationException("输出目录不能是文件系统根目录。");
        Directory.CreateDirectory(outputParent);
        string stagingDirectory = Path.Combine(
            outputParent,
            $".{Path.GetFileName(outputDirectory)}.boxforge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        int succeeded = 0;
        int skipped = 0;
        int failed = 0;

        try
        {
            var duplicateNames = inputFiles
                .GroupBy(
                    Path.GetFileNameWithoutExtension,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string inputFile in inputFiles)
            {
                string configName = Path.GetFileNameWithoutExtension(inputFile);
                if (string.IsNullOrWhiteSpace(configName))
                {
                    logger.LogError(
                        "配置文件名不能仅由扩展名组成：{InputFile}",
                        inputFile);
                    failed += options.Platforms.Count;
                    continue;
                }

                if (duplicateNames.Contains(configName))
                {
                    logger.LogError(
                        "配置名重复，无法确定输出目录：{ConfigName}",
                        configName);
                    failed += options.Platforms.Count;
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
                    logger.LogError(
                        ex,
                        "读取配置失败：{InputFile}",
                        inputFile);
                    failed += options.Platforms.Count;
                    continue;
                }

                foreach (var platform in options.Platforms)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relativePath = Path.Combine(
                        configName,
                        platform.ToString(),
                        "config.json");
                    string stagedFile = Path.Combine(
                        stagingDirectory,
                        relativePath);
                    string existingFile = Path.Combine(
                        outputDirectory,
                        relativePath);

                    try
                    {
                        var result = conversionService.Convert(
                            yamlContent,
                            platform,
                            strictNodeValidation: true);
                        Directory.CreateDirectory(
                            Path.GetDirectoryName(stagedFile)!);
                        await File.WriteAllTextAsync(
                            stagedFile,
                            result.JsonContent,
                            cancellationToken);

                        if (await HasSameContentAsync(
                            existingFile,
                            result.JsonContent,
                            cancellationToken))
                        {
                            skipped++;
                            logger.LogInformation(
                                "↷ 未变化：{ConfigName} [{Platform}]",
                                configName,
                                platform);
                        }
                        else
                        {
                            succeeded++;
                            logger.LogInformation(
                                "✓ 已生成：{ConfigName} [{Platform}]",
                                configName,
                                platform);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        logger.LogError(
                            ex,
                            "✗ 生成失败：{ConfigName} [{Platform}]：{Message}",
                            configName,
                            platform,
                            ex.Message);
                    }
                }
            }

            if (failed > 0)
            {
                logger.LogWarning(
                    "存在生成失败项，保留原输出目录不变。");
                return new LocalGenerationSummary(succeeded, skipped, failed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (succeeded == 0
                && HaveSameFileSet(stagingDirectory, outputDirectory))
            {
                return new LocalGenerationSummary(succeeded, skipped, failed);
            }

            ReplaceOutputDirectory(stagingDirectory, outputDirectory);
            stagingDirectory = "";
            return new LocalGenerationSummary(succeeded, skipped, failed);
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingDirectory)
                && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static bool IsSupportedInput(string path) =>
        SupportedExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase);

    private static bool IsSameOrChildPath(
        string candidatePath,
        string parentPath)
    {
        string normalizedParent = parentPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(
            normalizedParent,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasSameContentAsync(
        string path,
        string expectedContent,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        string existingContent = await File.ReadAllTextAsync(
            path,
            cancellationToken);
        return string.Equals(
            existingContent,
            expectedContent,
            StringComparison.Ordinal);
    }

    private static bool HaveSameFileSet(
        string stagingDirectory,
        string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return false;
        }

        string[] stagedFiles = [.. Directory
            .EnumerateFiles(
                stagingDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(stagingDirectory, path))
            .Order(StringComparer.OrdinalIgnoreCase)];
        string[] outputFiles = [.. Directory
            .EnumerateFiles(
                outputDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputDirectory, path))
            .Order(StringComparer.OrdinalIgnoreCase)];
        return stagedFiles.SequenceEqual(
            outputFiles,
            StringComparer.OrdinalIgnoreCase);
    }

    private void ReplaceOutputDirectory(
        string stagingDirectory,
        string outputDirectory)
    {
        string parentDirectory = Directory.GetParent(outputDirectory)!.FullName;
        string backupDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(outputDirectory)}.backup-{Guid.NewGuid():N}");
        bool oldOutputMoved = false;

        try
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Move(outputDirectory, backupDirectory);
                oldOutputMoved = true;
            }

            Directory.Move(stagingDirectory, outputDirectory);
        }
        catch
        {
            if (oldOutputMoved
                && !Directory.Exists(outputDirectory)
                && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, outputDirectory);
            }

            throw;
        }

        if (oldOutputMoved)
        {
            try
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "新输出已生效，但旧输出备份清理失败：{BackupDirectory}",
                    backupDirectory);
            }
        }
    }
}
