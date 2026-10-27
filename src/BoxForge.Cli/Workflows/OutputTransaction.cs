using BoxForge.Engine;
using Microsoft.Extensions.Logging;

namespace BoxForge.Workflows;

internal sealed partial class OutputTransaction : IDisposable
{
    private readonly string outputDirectory;
    private readonly ILogger logger;
    private readonly Action<string, string> moveDirectory;
    private string? stagingDirectory;
    private bool hasChanges;
    private readonly Dictionary<string, ConversionArtifact> artifacts = new(PathComparer);

    public OutputTransaction(
        string outputDirectory,
        ILogger logger,
        Action<string, string>? moveDirectory = null)
    {
        this.outputDirectory = outputDirectory;
        this.logger = logger;
        this.moveDirectory = moveDirectory ?? Directory.Move;
        string outputParent = Directory.GetParent(outputDirectory)?.FullName
            ?? throw new InvalidOperationException("输出目录不能是文件系统根目录。");
        if (Directory.Exists(outputParent)) ReportExistingTransactions(outputParent);
        stagingDirectory = Path.Combine(
            outputParent,
            $".{Path.GetFileName(outputDirectory)}.boxforge-{Guid.NewGuid():N}");
    }

    public async Task<bool> StageAsync(
        string configName,
        ConversionArtifact artifact,
        CancellationToken cancellationToken)
    {
        string staging = stagingDirectory ?? throw new ObjectDisposedException(nameof(OutputTransaction));
        string relativePath = Path.Combine(configName, artifact.Platform.ToString(), "config.json");
        string stagedFile = Path.GetFullPath(Path.Combine(staging, relativePath));
        string stagingRoot = Path.GetFullPath(staging)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!stagedFile.StartsWith(stagingRoot, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"配置输出路径必须位于暂存目录内：{configName}");
        }

        relativePath = Path.GetRelativePath(staging, stagedFile);
        string existingFile = Path.Combine(outputDirectory, relativePath);
        cancellationToken.ThrowIfCancellationRequested();
        bool changed = !File.Exists(existingFile)
            || !string.Equals(
                await File.ReadAllTextAsync(existingFile, cancellationToken),
                artifact.Content,
                StringComparison.Ordinal);
        artifacts[relativePath] = artifact;
        hasChanges |= changed;
        return changed;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        string staging = stagingDirectory ?? throw new ObjectDisposedException(nameof(OutputTransaction));
        cancellationToken.ThrowIfCancellationRequested();
        if (!hasChanges && HaveSameFileSet())
        {
            return;
        }

        string parentDirectory = Directory.GetParent(outputDirectory)!.FullName;
        Directory.CreateDirectory(parentDirectory);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(staging);
        else
            Directory.CreateDirectory(staging, PrivateDirectoryMode);
        foreach (var (relativePath, artifact) in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stagedFile = Path.Combine(staging, relativePath);
            string existingFile = Path.Combine(outputDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
            await File.WriteAllTextAsync(stagedFile, artifact.Content, cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stagedFile, File.Exists(existingFile)
                    ? File.GetUnixFileMode(existingFile)
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        cancellationToken.ThrowIfCancellationRequested();
        PreserveDirectoryPermissions(staging);
        cancellationToken.ThrowIfCancellationRequested();

        string backupDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(outputDirectory)}.backup-{Guid.NewGuid():N}");
        bool oldOutputMoved = false;

        try
        {
            if (Directory.Exists(outputDirectory))
            {
                moveDirectory(outputDirectory, backupDirectory);
                oldOutputMoved = true;
            }

            moveDirectory(staging, outputDirectory);
            stagingDirectory = null;
            artifacts.Clear();
        }
        catch (Exception publishFailure)
        {
            if (oldOutputMoved
                && !Directory.Exists(outputDirectory)
                && Directory.Exists(backupDirectory))
            {
                try
                {
                    moveDirectory(backupDirectory, outputDirectory);
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException(
                        $"发布输出和恢复旧输出均失败。旧输出保留在：{backupDirectory}",
                        publishFailure,
                        restoreFailure);
                }
            }

            throw;
        }

        if (oldOutputMoved)
        {
            try
            {
                DeleteTransactionDirectory(backupDirectory);
            }
            catch (Exception ex)
            {
                LogBackupCleanupFailure(logger, ex, backupDirectory);
            }
        }
    }

    public void Dispose()
    {
        string? staging = stagingDirectory;
        stagingDirectory = null;
        artifacts.Clear();
        if (staging is not null && Directory.Exists(staging))
        {
            try
            {
                DeleteTransactionDirectory(staging);
            }
            catch (Exception ex)
            {
                LogStagingCleanupFailure(logger, ex, staging);
            }
        }
    }

    private void ReportExistingTransactions(string parent)
    {
        string name = Path.GetFileName(outputDirectory);
        string[] prefixes = [$".{name}.boxforge-", $".{name}.backup-"];
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(parent))
            {
                string directoryName = Path.GetFileName(directory);
                if (prefixes.Any(prefix => directoryName.StartsWith(prefix, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && Guid.TryParseExact(directoryName[prefix.Length..], "N", out _)))
                {
                    LogExistingTransaction(logger, directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reading the parent is optional; write and traversal access suffice for publication.
            LogTransactionScanFailure(logger, ex, parent);
        }
    }

    private static void DeleteTransactionDirectory(string directory)
    {
        // A backup can contain links to external trees. Never follow them or change their modes.
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | PrivateDirectoryMode);
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            DeleteTransactionDirectory(child);
        }
        Directory.Delete(directory, recursive: true);
    }

    private bool HaveSameFileSet() => Directory.Exists(outputDirectory)
        && artifacts.Keys.Order(PathComparer).SequenceEqual(Directory
            .EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputDirectory, path))
            .Order(PathComparer), PathComparer);

    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private void PreserveDirectoryPermissions(string staging)
    {
        if (OperatingSystem.IsWindows()) return;

        // Finish writing before copying modes that may remove write or traversal access.
        // Materialize first and apply parents last, restoring the root mode only after its children.
        string[] directories = Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length).Append(staging).ToArray();
        foreach (string directory in directories)
        {
            string existingDirectory = Path.Combine(outputDirectory, Path.GetRelativePath(staging, directory));
            File.SetUnixFileMode(directory, Directory.Exists(existingDirectory)
                ? File.GetUnixFileMode(existingDirectory)
                : PrivateDirectoryMode);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    [LoggerMessage(21, LogLevel.Warning, "无法检查已有事务目录，将继续生成：{ParentDirectory}")]
    private static partial void LogTransactionScanFailure(ILogger logger, Exception exception, string parentDirectory);

    [LoggerMessage(20, LogLevel.Warning, "发现已有事务目录（可能为异常退出遗留或其他实例正在使用），已保留，请确认无生成进程后检查并手动恢复或清理：{Directory}")]
    private static partial void LogExistingTransaction(ILogger logger, string directory);

    [LoggerMessage(16, LogLevel.Warning, "暂存目录清理失败：{StagingDirectory}")]
    private static partial void LogStagingCleanupFailure(ILogger logger, Exception exception, string stagingDirectory);

    [LoggerMessage(17, LogLevel.Warning, "新输出已生效，但旧输出备份清理失败：{BackupDirectory}")]
    private static partial void LogBackupCleanupFailure(ILogger logger, Exception exception, string backupDirectory);
}
