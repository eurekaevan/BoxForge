using System.Net;
using BoxForge.Builders.Components;
using BoxForge.Services;
using Microsoft.Extensions.Logging;

namespace BoxForge.Tests;

internal sealed class StubAddressResolver(
    IReadOnlyList<IPAddress> addresses) : IHostAddressResolver
{
    public int CallCount { get; private set; }

    public IReadOnlyList<IPAddress> Resolve(string hostName)
    {
        CallCount++;
        return addresses;
    }
}

internal sealed class ThrowingAddressResolver : IHostAddressResolver
{
    public IReadOnlyList<IPAddress> Resolve(string hostName) =>
        throw new InvalidOperationException("DNS failure");
}

internal sealed class StubDbIpCityDatabase(
    IReadOnlyDictionary<IPAddress, string?> cities) : IDbIpCityDatabase
{
    public string? FindEnglishCity(IPAddress address) => cities[address];
}

internal sealed class ThrowingDbIpCityDatabase : IDbIpCityDatabase
{
    public string? FindEnglishCity(IPAddress address) =>
        throw new InvalidOperationException("DB-IP failure");
}

internal sealed class StubIp2LocationCityClient(
    IReadOnlyDictionary<IPAddress, string?> cities) : IIp2LocationCityClient
{
    public Task<string?> FindCityAsync(
        IPAddress address,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(cities[address]);
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
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return handler(request, cancellationToken);
    }
}

internal sealed class StubDbIpDatabaseSource(
    string databasePath) : IDbIpDatabaseSource
{
    public int CallCount { get; private set; }

    public string GetDatabasePath()
    {
        CallCount++;
        return databasePath;
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
