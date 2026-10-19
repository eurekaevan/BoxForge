using Microsoft.Extensions.Logging;

namespace BoxForge.Workflows;

internal sealed partial class InputDiscovery
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml"];
    private readonly ILogger logger;
    private readonly HashSet<string> duplicateNames;

    private InputDiscovery(string outputDirectory, string[] files, ILogger logger)
    {
        OutputDirectory = outputDirectory;
        Files = files;
        this.logger = logger;
        duplicateNames = files
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), PathComparer)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(PathComparer);
    }

    public string OutputDirectory { get; }
    public IReadOnlyList<string> Files { get; }

    public static InputDiscovery? Find(string inputPath, string outputPath, ILogger logger)
    {
        string inputDirectory = ResolvePhysicalPath(inputPath);
        string outputDirectory = ResolvePhysicalPath(outputPath);
        if (!Directory.Exists(inputDirectory))
        {
            LogMissingInputDirectory(logger, inputDirectory);
            return null;
        }

        if (string.Equals(
            inputDirectory.TrimEnd(Path.DirectorySeparatorChar),
            outputDirectory.TrimEnd(Path.DirectorySeparatorChar),
            PathComparison))
        {
            LogSameDirectories(logger);
            return null;
        }

        if (IsSameOrChildPath(inputDirectory, outputDirectory))
        {
            LogUnsafeOutputParent(logger);
            return null;
        }

        if (File.Exists(outputDirectory))
        {
            LogOutputIsFile(logger, outputDirectory);
            return null;
        }

        string[] inputFiles = [.. Directory
            .EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedInput)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];
        if (inputFiles.Length == 0)
        {
            LogNoInputFiles(logger, inputDirectory);
            return null;
        }

        foreach (string inputFile in inputFiles)
        {
            string physicalFile = ResolvePhysicalPath(inputFile);
            if (IsSameOrChildPath(physicalFile, outputDirectory))
            {
                LogInputInsideOutput(logger, inputFile, physicalFile);
                return null;
            }
        }

        return new InputDiscovery(outputDirectory, inputFiles, logger);
    }

    public string? GetConfigurationName(string inputFile)
    {
        string configName = Path.GetFileNameWithoutExtension(inputFile);
        if (string.IsNullOrWhiteSpace(configName))
        {
            LogEmptyConfigName(logger, inputFile);
            return null;
        }

        if (configName is "." or "..")
        {
            LogUnsafeConfigName(logger, inputFile);
            return null;
        }

        if (duplicateNames.Contains(configName))
        {
            LogDuplicateConfigName(logger, configName);
            return null;
        }

        return configName;
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
            FileSystemInfo? target = entry?.ResolveLinkTarget(returnFinalTarget: true);
            // A file link's final target can still contain directory links.
            current = target is null ? candidate : ResolvePhysicalPath(target.FullName);
        }

        return Path.GetFullPath(current);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

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

    [LoggerMessage(18, LogLevel.Error, "配置文件名不能使用目录导航名称 . 或 ..：{InputFile}")]
    private static partial void LogUnsafeConfigName(ILogger logger, string inputFile);

    [LoggerMessage(19, LogLevel.Error, "输入文件指向待替换的输出目录内，无法安全生成：{InputFile} → {PhysicalFile}")]
    private static partial void LogInputInsideOutput(ILogger logger, string inputFile, string physicalFile);
}
