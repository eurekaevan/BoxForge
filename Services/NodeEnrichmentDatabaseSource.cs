using System.IO.Compression;
using BoxForge.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoxForge.Services;

public interface INodeEnrichmentDatabaseSource
{
    string GetDatabasePath();
}

public sealed partial class NodeEnrichmentDatabaseSource :
    INodeEnrichmentDatabaseSource,
    IDisposable
{
    private readonly NodeEnrichmentOptions options;
    private readonly HttpClient httpClient;
    private readonly ILogger<NodeEnrichmentDatabaseSource> logger;
    private readonly Lazy<string> resolvedDatabasePath;
    private string? temporaryDirectory;

    public NodeEnrichmentDatabaseSource(
        IOptions<NodeEnrichmentOptions> options,
        HttpClient httpClient,
        ILogger<NodeEnrichmentDatabaseSource> logger)
    {
        this.options = options.Value;
        this.httpClient = httpClient;
        this.logger = logger;
        resolvedDatabasePath = new Lazy<string>(
            ResolveDatabasePath,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string GetDatabasePath() => resolvedDatabasePath.Value;

    public void Dispose() => DeleteTemporaryDirectory();

    private string ResolveDatabasePath()
    {
        string configuredPath = options.DatabasePath.Trim();
        if (!string.IsNullOrEmpty(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            LogMissingLocalDatabase(logger, fullPath);
        }

        string configuredUrl = options.DatabaseUrl.Trim();
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var databaseUri)
            || databaseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "节点城市数据库下载地址必须是 HTTP(S) URL。");
        }

        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"boxforge-node-enrichment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string databasePath = Path.Combine(
            temporaryDirectory,
            "GeoLite2-City.mmdb");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, databaseUri);
            using var response = httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();
            response.EnsureSuccessStatusCode();

            using Stream compressedStream = response.Content.ReadAsStream();
            using var gzipStream = new GZipStream(
                compressedStream,
                CompressionMode.Decompress);
            using var databaseStream = new FileStream(
                databasePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            gzipStream.CopyTo(databaseStream);
            return databasePath;
        }
        catch (Exception ex)
        {
            DeleteTemporaryDirectory();
            throw new InvalidOperationException(
                $"下载或解压节点城市数据库失败：{databaseUri}",
                ex);
        }
    }

    private void DeleteTemporaryDirectory()
    {
        string? directory = Interlocked.Exchange(
            ref temporaryDirectory,
            null);
        if (directory == null || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            LogTemporaryCleanupFailure(logger, ex, directory);
        }
    }

    [LoggerMessage(
        1,
        LogLevel.Warning,
        "节点城市数据库本地路径不存在，将改用下载地址：{DatabasePath}")]
    private static partial void LogMissingLocalDatabase(
        ILogger logger,
        string databasePath);

    [LoggerMessage(
        2,
        LogLevel.Warning,
        "节点城市数据库临时目录清理失败：{TemporaryDirectory}")]
    private static partial void LogTemporaryCleanupFailure(
        ILogger logger,
        Exception exception,
        string temporaryDirectory);
}
