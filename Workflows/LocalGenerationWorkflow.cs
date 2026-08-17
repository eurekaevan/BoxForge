using Microsoft.Extensions.Logging;
using BoxForge.Models;
using BoxForge.Services;

namespace BoxForge.Workflows;

public sealed partial class LocalGenerationWorkflow(
    ConversionService conversionService,
    ILogger<LocalGenerationWorkflow> logger) : ILocalGenerationWorkflow
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml"];

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

        string inputDirectory = ResolvePhysicalPath(request.InputDirectory);
        string outputDirectory = ResolvePhysicalPath(request.OutputDirectory);
        if (!Directory.Exists(inputDirectory))
        {
            LogMissingInputDirectory(logger, inputDirectory);
            return new LocalGenerationSummary(0, 0, 1);
        }

        if (string.Equals(
            inputDirectory.TrimEnd(Path.DirectorySeparatorChar),
            outputDirectory.TrimEnd(Path.DirectorySeparatorChar),
            PathComparison))
        {
            LogSameDirectories(logger);
            return new LocalGenerationSummary(0, 0, 1);
        }

        if (IsSameOrChildPath(inputDirectory, outputDirectory))
        {
            LogUnsafeOutputParent(logger);
            return new LocalGenerationSummary(0, 0, 1);
        }

        if (File.Exists(outputDirectory))
        {
            LogOutputIsFile(logger, outputDirectory);
            return new LocalGenerationSummary(0, 0, 1);
        }

        string[] inputFiles = [.. Directory
            .EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedInput)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];
        if (inputFiles.Length == 0)
        {
            LogNoInputFiles(logger, inputDirectory);
            return new LocalGenerationSummary(0, 0, 1);
        }

        string outputParent = Directory.GetParent(outputDirectory)?.FullName
            ?? throw new InvalidOperationException("输出目录不能是文件系统根目录。");
        Directory.CreateDirectory(outputParent);
        string stagingDirectory = Path.Combine(
            outputParent,
            $".{Path.GetFileName(outputDirectory)}.boxforge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        var changedItems = new List<(string ConfigName, TargetPlatform Platform)>();
        int skipped = 0;
        int failed = 0;

        try
        {
            var duplicateNames = inputFiles
                .GroupBy(
                    Path.GetFileNameWithoutExtension,
                    PathComparer)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(PathComparer);

            foreach (string inputFile in inputFiles)
            {
                string configName = Path.GetFileNameWithoutExtension(inputFile);
                if (string.IsNullOrWhiteSpace(configName))
                {
                    LogEmptyConfigName(logger, inputFile);
                    failed += request.Platforms.Count;
                    continue;
                }

                if (duplicateNames.Contains(configName))
                {
                    LogDuplicateConfigName(logger, configName);
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

                PreparedConversion prepared;
                try
                {
                    prepared = conversionService.Prepare(
                        yamlContent,
                        strictNodeValidation: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed += request.Platforms.Count;
                    LogPreparationFailure(logger, ex, configName);
                    continue;
                }

                foreach (var platform in request.Platforms)
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
                        string jsonContent = conversionService.Convert(
                            prepared,
                            platform);
                        Directory.CreateDirectory(
                            Path.GetDirectoryName(stagedFile)!);
                        await File.WriteAllTextAsync(
                            stagedFile,
                            jsonContent,
                            cancellationToken);

                        if (await HasSameContentAsync(
                            existingFile,
                            jsonContent,
                            cancellationToken))
                        {
                            skipped++;
                            LogUnchanged(logger, configName, platform);
                        }
                        else
                        {
                            changedItems.Add((configName, platform));
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
                return new LocalGenerationSummary(
                    0,
                    skipped,
                    failed,
                    changedItems.Count);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (changedItems.Count == 0
                && HaveSameFileSet(stagingDirectory, outputDirectory))
            {
                return new LocalGenerationSummary(0, skipped, 0);
            }

            ReplaceOutputDirectory(stagingDirectory, outputDirectory);
            stagingDirectory = "";
            foreach (var item in changedItems)
            {
                LogGenerated(logger, item.ConfigName, item.Platform);
            }

            return new LocalGenerationSummary(changedItems.Count, skipped, 0);
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingDirectory)
                && Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    LogStagingCleanupFailure(logger, ex, stagingDirectory);
                }
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
            PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string ResolvePhysicalPath(string path)
    {
        string current;
        string relativePath;
        if (Path.IsPathRooted(path))
        {
            current = Path.GetPathRoot(path)
                ?? throw new InvalidOperationException("路径缺少文件系统根目录。");
            relativePath = path[current.Length..];
        }
        else
        {
            current = Environment.CurrentDirectory;
            relativePath = path;
        }

        foreach (string segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Directory.GetParent(current)?.FullName ?? current;
                continue;
            }

            string candidate = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            current = entry?.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? candidate;
        }

        return Path.GetFullPath(current);
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
            .Order(PathComparer)];
        string[] outputFiles = [.. Directory
            .EnumerateFiles(
                outputDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputDirectory, path))
            .Order(PathComparer)];
        return stagedFiles.SequenceEqual(
            outputFiles,
            PathComparer);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

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
                LogBackupCleanupFailure(logger, ex, backupDirectory);
            }
        }
    }

    [LoggerMessage(1, LogLevel.Error, "输入目录和输出目录不能为空。")]
    private static partial void LogEmptyDirectories(ILogger logger);

    [LoggerMessage(2, LogLevel.Error, "目标平台必须非空、有效且不重复。")]
    private static partial void LogInvalidPlatforms(ILogger logger);

    [LoggerMessage(3, LogLevel.Error, "输入目录不存在：{InputDirectory}")]
    private static partial void LogMissingInputDirectory(ILogger logger, string inputDirectory);

    [LoggerMessage(4, LogLevel.Error, "输入目录与输出目录不能相同。")]
    private static partial void LogSameDirectories(ILogger logger);

    [LoggerMessage(5, LogLevel.Error, "输出目录不能是输入目录的父目录，否则替换输出时会删除输入。")]
    private static partial void LogUnsafeOutputParent(ILogger logger);

    [LoggerMessage(6, LogLevel.Error, "输出路径已存在且不是目录：{OutputDirectory}")]
    private static partial void LogOutputIsFile(ILogger logger, string outputDirectory);

    [LoggerMessage(7, LogLevel.Error, "输入目录内没有找到 .yaml 或 .yml 文件：{InputDirectory}")]
    private static partial void LogNoInputFiles(ILogger logger, string inputDirectory);

    [LoggerMessage(8, LogLevel.Error, "配置文件名不能仅由扩展名组成：{InputFile}")]
    private static partial void LogEmptyConfigName(ILogger logger, string inputFile);

    [LoggerMessage(9, LogLevel.Error, "配置名重复，无法确定输出目录：{ConfigName}")]
    private static partial void LogDuplicateConfigName(ILogger logger, string configName);

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

    [LoggerMessage(16, LogLevel.Warning, "暂存目录清理失败：{StagingDirectory}")]
    private static partial void LogStagingCleanupFailure(ILogger logger, Exception exception, string stagingDirectory);

    [LoggerMessage(17, LogLevel.Warning, "新输出已生效，但旧输出备份清理失败：{BackupDirectory}")]
    private static partial void LogBackupCleanupFailure(ILogger logger, Exception exception, string backupDirectory);
}
