namespace BoxForge.Configuration;

public sealed class NodeEnrichmentOptions
{
    public const string DefaultDatabaseUrl =
        "https://cdn.jsdelivr.net/npm/geolite2-city/GeoLite2-City.mmdb.gz";

    public string DatabasePath { get; set; } = "";
    public string DatabaseUrl { get; set; } = DefaultDatabaseUrl;
}
