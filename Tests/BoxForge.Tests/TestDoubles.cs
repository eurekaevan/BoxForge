using System.Net;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Logging;

namespace BoxForge.Tests;

internal sealed class StubExitIpDetector(
    IReadOnlyList<IPAddress?> exitAddresses,
    TimeSpan? delay = null) : IExitIpDetector
{
    public int CallCount { get; private set; }
    public int MaximumConcurrency { get; private set; }
    public List<ProxyOutbound> Outbounds { get; } = [];
    private int activeCount;

    public async Task<IPAddress?> DetectAsync(
        ProxyOutbound outbound,
        CancellationToken cancellationToken = default)
    {
        int index = CallCount;
        CallCount++;
        Outbounds.Add(outbound);
        int active = Interlocked.Increment(ref activeCount);
        MaximumConcurrency = Math.Max(MaximumConcurrency, active);
        try
        {
            if (delay.HasValue)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }

            return exitAddresses[index];
        }
        finally
        {
            Interlocked.Decrement(ref activeCount);
        }
    }
}

internal sealed class ThrowingExitIpDetector : IExitIpDetector
{
    public Task<IPAddress?> DetectAsync(
        ProxyOutbound outbound,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Exit detection must not run");
}

internal sealed class StubDbIpCityDatabase(
    IReadOnlyDictionary<IPAddress, string?> cities) : IDbIpCityDatabase
{
    public int InitializeCount { get; private set; }
    public int LookupCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCount++;
        return Task.CompletedTask;
    }

    public string? FindEnglishCity(IPAddress address)
    {
        LookupCount++;
        return cities[address];
    }
}

internal sealed class ThrowingDbIpCityDatabase : IDbIpCityDatabase
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public string? FindEnglishCity(IPAddress address) =>
        throw new InvalidOperationException("DB-IP failure");
}

internal sealed class ThrowingInitializingDbIpCityDatabase : IDbIpCityDatabase
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("DB-IP init failure"));

    public string? FindEnglishCity(IPAddress address) =>
        throw new InvalidOperationException("DB-IP must not be queried");
}

internal sealed class StubIp2LocationCityClient(
    IReadOnlyDictionary<IPAddress, string?> cities) : IIp2LocationCityClient
{
    public int CallCount { get; private set; }

    public Task<string?> FindCityAsync(
        IPAddress address,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(cities[address]);
    }
}

internal sealed class ThrowingIp2LocationCityClient : IIp2LocationCityClient
{
    public Task<string?> FindCityAsync(
        IPAddress address,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("IP2Location failure");
}

internal sealed class StubHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) :
    HttpMessageHandler
{
    private int callCount;

    public int CallCount => Volatile.Read(ref callCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref callCount);
        return handler(request, cancellationToken);
    }
}

internal sealed class StubExitIpFetcher(IPAddress? exitAddress) : IExitIpFetcher
{
    public int CallCount { get; private set; }
    public int SocksPort { get; private set; }

    public Task<IPAddress?> FetchAsync(
        int socksPort,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        SocksPort = socksPort;
        return Task.FromResult(exitAddress);
    }
}

internal sealed class StubProbeServerResolver(string resolvedServer) :
    IProbeServerResolver
{
    public int CallCount { get; private set; }
    public string? OriginalServer { get; private set; }

    public Task<string> ResolveAsync(
        string server,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        OriginalServer = server;
        return Task.FromResult(resolvedServer);
    }
}

internal sealed class ThrowingExitIpFetcher : IExitIpFetcher
{
    public Task<IPAddress?> FetchAsync(
        int socksPort,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("ipify failure");
}

internal sealed class StubSingboxProcessLauncher : ISingboxProcessLauncher
{
    public StubSingboxProcess Process { get; } = new();
    public string? Executable { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? ConfigContent { get; private set; }
    public int SocksPort { get; private set; }

    public ISingboxProcess Start(
        string executable,
        string configPath,
        int socksPort)
    {
        Executable = executable;
        ConfigPath = configPath;
        ConfigContent = File.ReadAllText(configPath);
        SocksPort = socksPort;
        return Process;
    }
}

internal sealed class StubSingboxProcess : ISingboxProcess
{
    public int WaitCount { get; private set; }
    public int DisposeCount { get; private set; }

    public Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default)
    {
        WaitCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class StubDbIpDatabaseSource(
    string databasePath) : IDbIpDatabaseSource
{
    public int CallCount { get; private set; }

    public Task<string> GetDatabasePathAsync(
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(databasePath);
    }
}

internal sealed class StubDbIpReaderFactory(string city) : IDbIpCityReaderFactory
{
    public StubDbIpCityReader Reader { get; } = new(city);
    public int OpenCount { get; private set; }

    public IDbIpCityReader Open(string databasePath)
    {
        OpenCount++;
        return Reader;
    }
}

internal sealed class StubDbIpCityReader(string city) : IDbIpCityReader
{
    public int LookupCount { get; private set; }

    public string? FindEnglishCity(IPAddress address)
    {
        LookupCount++;
        return city;
    }

    public void Dispose()
    {
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Levels { get; } = [];
    public List<string> Messages { get; } = [];
    public List<Exception> Exceptions { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Levels.Add(logLevel);
        Messages.Add(formatter(state, exception));
        if (exception != null)
        {
            Exceptions.Add(exception);
        }
    }
}
