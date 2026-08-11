namespace BoxForge.Configuration;

public sealed class NodeEnrichmentOptions
{
    public const string DefaultDatabaseUrl =
        "https://cdn.jsdelivr.net/npm/dbip-city-lite/dbip-city-lite.mmdb.gz";

    public string DatabasePath { get; set; } = "";
    public string DatabaseUrl { get; set; } = DefaultDatabaseUrl;
}
