using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoxForge.Services;

public interface IExitIpDetector
{
    Task<IPAddress?> DetectAsync(
        ProxyOutbound outbound,
        CancellationToken cancellationToken = default);
}

public interface IExitIpFetcher
{
    Task<IPAddress?> FetchAsync(
        int socksPort,
        CancellationToken cancellationToken = default);
}

public interface IExitIpHttpClientFactory
{
    HttpClient Create(int socksPort);
}

public sealed class ExitIpFetchException(string reason) : Exception
{
    public string Reason { get; } = reason;
}

public interface ISingboxExecutableValidator
{
    Task<SingboxExecutableValidationResult> ValidateAsync(string executable);
}

public sealed record SingboxExecutableValidationResult(
    bool IsValid,
    string Reason);

public interface ISingboxProcessLauncher
{
    ISingboxProcess Start(string executable, string configPath, int socksPort);
}

public interface ISingboxProcess : IAsyncDisposable
{
    string? FailureReason { get; }

    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);
}

public sealed partial class SingboxExitIpDetector(
    IOptions<NodeEnrichmentOptions> options,
    ISingboxExecutableValidator executableValidator,
    ISingboxProcessLauncher processLauncher,
    IExitIpFetcher exitIpFetcher,
    ILogger<SingboxExitIpDetector> logger) : IExitIpDetector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NodeEnrichmentOptions enrichmentOptions = options.Value;
    private readonly Lazy<Task<SingboxExecutableValidationResult>>
        executableValidation = new(
            () => executableValidator.ValidateAsync(
                options.Value.SingBoxPath.Trim()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    private int executableFailureLogged;

    public async Task<IPAddress?> DetectAsync(
        ProxyOutbound outbound,
        CancellationToken cancellationToken = default)
    {
        SingboxExecutableValidationResult validation;
        try
        {
            validation = await executableValidation.Value;
        }
        catch (Exception)
        {
            validation = new(false, "validation-failed");
        }

        if (!validation.IsValid)
        {
            if (Interlocked.Exchange(ref executableFailureLogged, 1) == 0)
            {
                LogInvalidExecutable(
                    logger,
                    enrichmentOptions.SingBoxPath.Trim(),
                    validation.Reason);
            }

            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);
        CancellationToken probeToken = timeoutSource.Token;

        string? temporaryDirectory = null;
        ISingboxProcess? process = null;
        string stage = "create-config";
        try
        {
            int socksPort = ReserveLoopbackPort();
            temporaryDirectory = Directory.CreateTempSubdirectory(
                "boxforge-exit-").FullName;
            string configPath = Path.Combine(temporaryDirectory, "config.json");
            await WriteProbeConfigAsync(
                configPath,
                socksPort,
                outbound,
                probeToken);

            stage = "start-sing-box";
            process = processLauncher.Start(
                enrichmentOptions.SingBoxPath.Trim(),
                configPath,
                socksPort);
            stage = "wait-local-proxy";
            await process.WaitUntilReadyAsync(probeToken);
            stage = "fetch-ipify";
            IPAddress? exitAddress = await exitIpFetcher.FetchAsync(
                socksPort,
                probeToken);
            if (exitAddress == null)
            {
                LogIpifyRejected(logger, outbound.Tag);
            }

            return exitAddress;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            LogProbeTimeout(logger, outbound.Tag);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ExitIpFetchException ex)
        {
            string reason = process?.FailureReason is { } singboxReason
                ? $"{ex.Reason},sing-box:{singboxReason}"
                : ex.Reason;
            LogIpifyFailure(logger, outbound.Tag, reason);
            return null;
        }
        catch (Exception)
        {
            LogProbeFailure(logger, outbound.Tag, stage);
            return null;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    await process.DisposeAsync();
                }
                catch (Exception)
                {
                    LogProcessCleanupFailure(logger, outbound.Tag);
                }
            }

            if (temporaryDirectory != null)
            {
                try
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
                catch (Exception)
                {
                    LogFileCleanupFailure(logger, outbound.Tag);
                }
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteProbeConfigAsync(
        string configPath,
        int socksPort,
        ProxyOutbound outbound,
        CancellationToken cancellationToken)
    {
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead
                | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(configPath, fileOptions);
        await JsonSerializer.SerializeAsync(
            stream,
            CreateProbeConfig(socksPort, outbound),
            JsonOptions,
            cancellationToken);
    }

    private static ExitProbeConfig CreateProbeConfig(
        int socksPort,
        ProxyOutbound outbound)
    {
        ExitProbeDns? dns = CreateProbeDns(outbound);
        ProxyOutbound probeOutbound = outbound with
        {
            DomainResolver = dns?.Final!
        };
        return new ExitProbeConfig(
            dns,
            [
                new Inbound
                {
                    Type = "mixed",
                    Tag = "boxforge-exit-in",
                    Listen = IPAddress.Loopback.ToString(),
                    ListenPort = socksPort
                }
            ],
            [probeOutbound],
            new ExitProbeRoute(probeOutbound.Tag, true));
    }

    private static ExitProbeDns? CreateProbeDns(ProxyOutbound outbound)
    {
        if (IPAddress.TryParse(outbound.Server, out _))
        {
            return null;
        }

        ExitProbeDnsServer server = outbound.ProbeDnsServers
            .Select(CreateProbeDnsServer)
            .FirstOrDefault(candidate => candidate != null)
            ?? new ExitProbeDnsServer(
                "https",
                "boxforge-node-dns",
                "223.5.5.5",
                null,
                null,
                new DnsTlsConfig { ServerName = "dns.alidns.com" });
        return new ExitProbeDns([server], server.Tag, DnsStrategy.Ipv4Only);
    }

    private static ExitProbeDnsServer? CreateProbeDnsServer(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        return new ExitProbeDnsServer(
            "https",
            "boxforge-node-dns",
            uri.Host,
            uri.IsDefaultPort ? null : uri.Port,
            uri.AbsolutePath == "/" ? null : uri.AbsolutePath,
            new DnsTlsConfig { ServerName = uri.Host });
    }

    [LoggerMessage(
        1,
        LogLevel.Warning,
        "节点出口 IP 检测超时，保留原 tag：{Tag}")]
    private static partial void LogProbeTimeout(ILogger logger, string tag);

    [LoggerMessage(
        2,
        LogLevel.Warning,
        "节点出口 IP 检测失败，stage={Stage}，保留原 tag：{Tag}")]
    private static partial void LogProbeFailure(
        ILogger logger,
        string tag,
        string stage);

    [LoggerMessage(
        3,
        LogLevel.Warning,
        "sing-box 测试进程清理失败：{Tag}")]
    private static partial void LogProcessCleanupFailure(
        ILogger logger,
        string tag);

    [LoggerMessage(
        4,
        LogLevel.Warning,
        "sing-box 临时文件清理失败：{Tag}")]
    private static partial void LogFileCleanupFailure(ILogger logger, string tag);

    [LoggerMessage(
        5,
        LogLevel.Warning,
        "节点出口 IP 检测已跳过：{Path} 不是可用的 SagerNet sing-box CLI，"
            + "reason={Reason}。请将 NodeEnrichment:SingBoxPath 指向官方 core 可执行文件。")]
    private static partial void LogInvalidExecutable(
        ILogger logger,
        string path,
        string reason);

    [LoggerMessage(
        6,
        LogLevel.Warning,
        "ipify 未返回有效出口 IP，保留原 tag：{Tag}")]
    private static partial void LogIpifyRejected(ILogger logger, string tag);

    [LoggerMessage(
        7,
        LogLevel.Warning,
        "节点出口 IP 请求失败，reason={Reason}，保留原 tag：{Tag}")]
    private static partial void LogIpifyFailure(
        ILogger logger,
        string tag,
        string reason);

    private sealed record ExitProbeConfig(
        [property: JsonPropertyName("dns")]
        ExitProbeDns? Dns,
        [property: JsonPropertyName("inbounds")] List<Inbound> Inbounds,
        [property: JsonPropertyName("outbounds")] List<Outbound> Outbounds,
        [property: JsonPropertyName("route")] ExitProbeRoute Route);

    private sealed record ExitProbeRoute(
        [property: JsonPropertyName("final")] string Final,
        [property: JsonPropertyName("auto_detect_interface")]
        bool AutoDetectInterface);

    private sealed record ExitProbeDns(
        [property: JsonPropertyName("servers")]
        List<ExitProbeDnsServer> Servers,
        [property: JsonPropertyName("final")] string Final,
        [property: JsonPropertyName("strategy")] DnsStrategy Strategy);

    private sealed record ExitProbeDnsServer(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("tag")] string Tag,
        [property: JsonPropertyName("server")] string Server,
        [property: JsonPropertyName("server_port")]
        int? ServerPort,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("tls")] DnsTlsConfig Tls);
}

public sealed class SingboxExecutableValidator : ISingboxExecutableValidator
{
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(3);

    public async Task<SingboxExecutableValidationResult> ValidateAsync(
        string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("version");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new(false, "start-failed");
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            using var timeoutSource = new CancellationTokenSource(
                ValidationTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await Task.WhenAll(standardOutput, standardError);
                return new(false, "version-timeout");
            }

            string output = await standardOutput;
            await standardError;
            bool isCli = process.ExitCode == 0
                && output.StartsWith(
                    "sing-box version ",
                    StringComparison.Ordinal);
            return isCli
                ? new(true, "ok")
                : new(false, "not-sagernet-cli");
        }
        catch (Exception)
        {
            TryKill(process);
            return new(false, "start-failed");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (Exception)
        {
            // The process has already exited or cannot be controlled.
        }
    }
}

public sealed class ExitIpFetcher(
    IExitIpHttpClientFactory httpClientFactory) : IExitIpFetcher
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);
    private static readonly (string Name, Uri Uri)[] IpifyEndpoints =
    [
        ("ipv4", new Uri("https://api.ipify.org")),
        ("universal", new Uri("https://api64.ipify.org"))
    ];

    public async Task<IPAddress?> FetchAsync(
        int socksPort,
        CancellationToken cancellationToken = default)
    {
        using HttpClient httpClient = httpClientFactory.Create(socksPort);
        var failures = new List<string>(IpifyEndpoints.Length);
        foreach ((string name, Uri endpoint) in IpifyEndpoints)
        {
            using var attemptSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            attemptSource.CancelAfter(AttemptTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using HttpResponseMessage response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptSource.Token);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{name}:http-{(int)response.StatusCode}");
                    continue;
                }

                string responseBody = await response.Content.ReadAsStringAsync(
                    attemptSource.Token);
                if (IPAddress.TryParse(responseBody.Trim(), out var address))
                {
                    return address;
                }

                failures.Add($"{name}:invalid-response");
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{name}:timeout");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                failures.Add($"{name}:{Classify(ex)}");
            }
            catch (Exception)
            {
                failures.Add($"{name}:unexpected-error");
            }
        }

        throw new ExitIpFetchException(string.Join(',', failures));
    }

    private static string Classify(HttpRequestException exception)
    {
        string reason = exception.HttpRequestError.ToString();
        Exception? inner = exception.InnerException;
        while (inner != null)
        {
            if (inner is SocketException socketException)
            {
                return $"{reason}/{socketException.SocketErrorCode}";
            }

            inner = inner.InnerException;
        }

        return reason;
    }
}

public sealed class ExitIpHttpClientFactory : IExitIpHttpClientFactory
{
    public HttpClient Create(int socksPort)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}")
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

public sealed class SingboxProcessLauncher : ISingboxProcessLauncher
{
    public ISingboxProcess Start(
        string executable,
        string configPath,
        int socksPort)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var diagnostics = new SingboxProcessDiagnostics();
        process.OutputDataReceived += diagnostics.Observe;
        process.ErrorDataReceived += diagnostics.Observe;
        bool started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("sing-box 进程未能启动。");
            }

            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new SingboxProcess(process, socksPort, diagnostics);
        }
        catch
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process.Dispose();
            throw;
        }
    }
}

internal sealed class SingboxProcess(
    Process process,
    int socksPort,
    SingboxProcessDiagnostics diagnostics) :
    ISingboxProcess
{
    private static readonly TimeSpan ReadinessPollInterval =
        TimeSpan.FromMilliseconds(50);

    public string? FailureReason => diagnostics.FailureReason;

    public async Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "sing-box 在本地代理就绪前退出。");
            }

            using var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(
                    IPAddress.Loopback,
                    socksPort,
                    cancellationToken);
                return;
            }
            catch (SocketException) when (!process.HasExited)
            {
                await Task.Delay(ReadinessPollInterval, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
        finally
        {
            process.Dispose();
        }
    }
}
