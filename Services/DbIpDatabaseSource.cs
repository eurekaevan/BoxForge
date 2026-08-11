using System.IO.Compression;
using BoxForge.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoxForge.Services;

public interface IDbIpDatabaseSource
{
    string GetDatabasePath();
}

public sealed partial class DbIpDatabaseSource : IDbIpDatabaseSource, IDisposable
{
    private readonly NodeEnrichmentOptions options;
    private readonly HttpClient httpClient;
    private readonly ILogger<DbIpDatabaseSource> logger;
    private readonly Lazy<string> resolvedDatabasePath;
    private string? temporaryDirectory;

    public DbIpDatabaseSource(
        IOptions<NodeEnrichmentOptions> options,
        HttpClient httpClient,
        ILogger<DbIpDatabaseSource> logger)
    {
        this.options = options.Value;
        this.httpClient = httpClient;
        this.logger = logger;
        resolvedDatabasePath = new Lazy<string>(
            DownloadAndExtract,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string GetDatabasePath() => resolvedDatabasePath.Value;

    public void Dispose() => DeleteTemporaryDirectory();

    private string DownloadAndExtract()
    {
        string configuredUrl = options.DbIpDatabaseUrl.Trim();
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var databaseUri)
            || databaseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "DB-IP City Lite 数据库下载地址必须是 HTTP(S) URL。");
        }

        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"boxforge-dbip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string databasePath = Path.Combine(
            temporaryDirectory,
            "dbip-city-lite.mmdb");

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
                "下载或解压 DB-IP City Lite 数据库失败。",
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
        "清理 DB-IP City Lite 临时目录失败：{Directory}")]
    private static partial void LogTemporaryCleanupFailure(
        ILogger logger,
        Exception exception,
        string directory);
}
