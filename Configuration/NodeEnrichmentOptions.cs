namespace BoxForge.Configuration;

public sealed class NodeEnrichmentOptions
{
    public const string DefaultDbIpDatabaseUrl =
        "https://cdn.jsdelivr.net/npm/dbip-city-lite/dbip-city-lite.mmdb.gz";

    public bool Enabled { get; set; } = true;
    public string Ip2LocationApiKey { get; set; } = "";
    public string DbIpDatabaseUrl { get; set; } = DefaultDbIpDatabaseUrl;
}
