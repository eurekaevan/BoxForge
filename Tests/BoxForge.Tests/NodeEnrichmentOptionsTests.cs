using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeEnrichmentOptionsTests
{
    [Test]
    public void DefaultsEnableBothSourcesAndUseDbIpCityLiteUrl()
    {
        using ServiceProvider provider = BuildProvider([]);

        NodeEnrichmentOptions options = provider
            .GetRequiredService<IOptions<NodeEnrichmentOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.Ip2LocationApiKey, Is.Empty);
            Assert.That(
                options.DbIpDatabaseUrl,
                Is.EqualTo(NodeEnrichmentOptions.DefaultDbIpDatabaseUrl));
        });
    }

    [Test]
    public void NestedConfigurationBindsAllNodeEnrichmentSettings()
    {
        var values = new Dictionary<string, string?>
        {
            ["NodeEnrichment:Enabled"] = "false",
            ["NodeEnrichment:Ip2LocationApiKey"] = "api-secret",
            ["NodeEnrichment:DbIpDatabaseUrl"] =
                "https://example.test/dbip.mmdb.gz"
        };
        using ServiceProvider provider = BuildProvider(values);

        NodeEnrichmentOptions options = provider
            .GetRequiredService<IOptions<NodeEnrichmentOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.False);
            Assert.That(options.Ip2LocationApiKey, Is.EqualTo("api-secret"));
            Assert.That(
                options.DbIpDatabaseUrl,
                Is.EqualTo("https://example.test/dbip.mmdb.gz"));
        });
    }

    private static ServiceProvider BuildProvider(
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddBoxForgeOptions(configuration);
        return services.BuildServiceProvider();
    }
}
