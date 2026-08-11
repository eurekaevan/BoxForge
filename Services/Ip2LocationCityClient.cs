using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BoxForge.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoxForge.Services;

public interface IIp2LocationCityClient
{
    Task<string?> FindCityAsync(
        IPAddress address,
        CancellationToken cancellationToken = default);
}

public sealed partial class Ip2LocationCityClient :
    IIp2LocationCityClient,
    IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<IPAddress, Lazy<Task<string?>>> cache = [];
    private readonly SemaphoreSlim concurrencyLimiter = new(4, 4);
    private readonly NodeEnrichmentOptions options;
    private readonly HttpClient httpClient;
    private readonly ILogger<Ip2LocationCityClient> logger;

    public Ip2LocationCityClient(
        IOptions<NodeEnrichmentOptions> options,
        HttpClient httpClient,
        ILogger<Ip2LocationCityClient> logger)
    {
        this.options = options.Value;
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public Task<string?> FindCityAsync(
        IPAddress address,
        CancellationToken cancellationToken = default)
    {
        return cache.GetOrAdd(
            address,
            key => new Lazy<Task<string?>>(
                () => RequestCityAsync(key, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public void Dispose() => concurrencyLimiter.Dispose();

    private async Task<string?> RequestCityAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);
        CancellationToken requestToken = timeoutSource.Token;
        bool entered = false;

        try
        {
            await concurrencyLimiter.WaitAsync(requestToken);
            entered = true;

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildRequestUri(address));
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                LogRateLimited(logger, address);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogRequestRejected(logger, address, (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<Ip2LocationResponse>(
                cancellationToken: requestToken);
            return payload?.CityName;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogRequestTimeout(logger, address);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            LogRequestFailure(logger, address);
            return null;
        }
        finally
        {
            if (entered)
            {
                concurrencyLimiter.Release();
            }
        }
    }

    private Uri BuildRequestUri(IPAddress address)
    {
        string apiKey = options.Ip2LocationApiKey.Trim();
        string query = string.IsNullOrEmpty(apiKey)
            ? $"ip={Uri.EscapeDataString(address.ToString())}"
            : $"key={Uri.EscapeDataString(apiKey)}&ip={Uri.EscapeDataString(address.ToString())}";
        return new UriBuilder("https://api.ip2location.io/")
        {
            Query = query
        }.Uri;
    }

    [LoggerMessage(
        1,
        LogLevel.Warning,
        "IP2Location.io 城市查询被限流，address={Address}")]
    private static partial void LogRateLimited(ILogger logger, IPAddress address);

    [LoggerMessage(
        2,
        LogLevel.Warning,
        "IP2Location.io 城市查询失败，address={Address}，HTTP={StatusCode}")]
    private static partial void LogRequestRejected(
        ILogger logger,
        IPAddress address,
        int statusCode);

    [LoggerMessage(
        3,
        LogLevel.Warning,
        "IP2Location.io 城市查询超时，address={Address}")]
    private static partial void LogRequestTimeout(ILogger logger, IPAddress address);

    [LoggerMessage(
        4,
        LogLevel.Warning,
        "IP2Location.io 城市查询失败，address={Address}")]
    private static partial void LogRequestFailure(ILogger logger, IPAddress address);

    private sealed record Ip2LocationResponse(
        [property: JsonPropertyName("city_name")] string? CityName);
}
