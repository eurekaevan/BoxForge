using System.Net;
using MaxMind.GeoIP2;

namespace BoxForge.Services;

public interface IDbIpCityDatabase
{
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
    private readonly Lazy<IDbIpCityReader> reader;

    public DbIpCityDatabase(
        IDbIpDatabaseSource databaseSource,
        IDbIpCityReaderFactory readerFactory)
    {
        reader = new Lazy<IDbIpCityReader>(
            () => readerFactory.Open(databaseSource.GetDatabasePath()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string? FindEnglishCity(IPAddress address) =>
        reader.Value.FindEnglishCity(address);

    public void Dispose()
    {
        if (reader.IsValueCreated)
        {
            reader.Value.Dispose();
        }
    }
}
