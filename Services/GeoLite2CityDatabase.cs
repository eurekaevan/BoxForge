using System.Net;
using MaxMind.GeoIP2;

namespace BoxForge.Services;

public interface ICityDatabase
{
    string? FindEnglishCity(IPAddress address);
}

public interface IGeoLite2CityReader : IDisposable
{
    string? FindEnglishCity(IPAddress address);
}

public interface IGeoLite2CityReaderFactory
{
    IGeoLite2CityReader Open(string databasePath);
}

public sealed class MaxMindGeoLite2CityReaderFactory :
    IGeoLite2CityReaderFactory
{
    public IGeoLite2CityReader Open(string databasePath) =>
        new MaxMindGeoLite2CityReader(new DatabaseReader(databasePath));
}

public sealed class MaxMindGeoLite2CityReader(
    DatabaseReader reader) : IGeoLite2CityReader
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

public sealed class GeoLite2CityDatabase : ICityDatabase, IDisposable
{
    private readonly Lazy<IGeoLite2CityReader> reader;

    public GeoLite2CityDatabase(
        INodeEnrichmentDatabaseSource databaseSource,
        IGeoLite2CityReaderFactory readerFactory)
    {
        reader = new Lazy<IGeoLite2CityReader>(
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
