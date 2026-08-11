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

internal sealed class StubCityDatabase(
    IReadOnlyDictionary<IPAddress, string?> cities) : ICityDatabase
{
    public string? FindEnglishCity(IPAddress address) => cities[address];
}

internal sealed class ThrowingCityDatabase : ICityDatabase
{
    public string? FindEnglishCity(IPAddress address) =>
        throw new InvalidOperationException("database failure");
}

internal sealed class StubHttpHandler(
    byte[] responseContent,
    HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(responseContent)
        });
    }
}

internal sealed class ThrowingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("HTTP should not be used");
}

internal sealed class StubDatabaseSource(
    string databasePath) : INodeEnrichmentDatabaseSource
{
    public int CallCount { get; private set; }

    public string GetDatabasePath()
    {
        CallCount++;
        return databasePath;
    }
}

internal sealed class StubReaderFactory(string city) : IGeoLite2CityReaderFactory
{
    public StubGeoLite2CityReader Reader { get; } = new(city);
    public int OpenCount { get; private set; }

    public IGeoLite2CityReader Open(string databasePath)
    {
        OpenCount++;
        return Reader;
    }
}

internal sealed class StubGeoLite2CityReader(
    string city) : IGeoLite2CityReader
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

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Levels.Add(logLevel);
}
