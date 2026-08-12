using System.Net;
using MaxMind.GeoIP2;

namespace BoxForge.Services;

public interface IDbIpCityDatabase
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    string? FindEnglishCity(IPAddress address);
}

public interface IDbIpCityReader : IDisposable
{
    string? FindEnglishCity(IPAddress address);
}

public interface IDbIpCityReaderFactory
{
    IDbIpCityReader Open(string databasePath);
}

public sealed class MaxMindDbIpCityReaderFactory : IDbIpCityReaderFactory
{
    public IDbIpCityReader Open(string databasePath) =>
        new MaxMindDbIpCityReader(new DatabaseReader(databasePath));
}

public sealed class MaxMindDbIpCityReader(
    DatabaseReader reader) : IDbIpCityReader
{
    public string? FindEnglishCity(IPAddress address)
    {
        var city = reader.City(address).City;
        return city.Names.TryGetValue("en", out string? englishName)
            ? englishName
            : null;
    }

    public void Dispose() => reader.Dispose();
}

public sealed class DbIpCityDatabase : IDbIpCityDatabase, IDisposable
{
    private readonly IDbIpDatabaseSource databaseSource;
    private readonly IDbIpCityReaderFactory readerFactory;
    private readonly object initializationLock = new();
    private Task? initializationTask;
    private IDbIpCityReader? reader;

    public DbIpCityDatabase(
        IDbIpDatabaseSource databaseSource,
        IDbIpCityReaderFactory readerFactory)
    {
        this.databaseSource = databaseSource;
        this.readerFactory = readerFactory;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task currentInitialization;
        lock (initializationLock)
        {
            currentInitialization = initializationTask ??=
                InitializeCoreAsync(cancellationToken);
        }

        return currentInitialization.WaitAsync(cancellationToken);
    }

    public string? FindEnglishCity(IPAddress address) =>
        (reader ?? throw new InvalidOperationException(
            "DB-IP City Lite 数据库尚未初始化。"))
        .FindEnglishCity(address);

    public void Dispose() => Interlocked.Exchange(ref reader, null)?.Dispose();

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        string databasePath = await databaseSource.GetDatabasePathAsync(
            cancellationToken);
        IDbIpCityReader createdReader = readerFactory.Open(databasePath);
        Interlocked.Exchange(ref reader, createdReader)?.Dispose();
    }
}
