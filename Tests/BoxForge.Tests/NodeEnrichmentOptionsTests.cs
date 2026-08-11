using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeEnrichmentOptionsTests
{
    [Test]
    public void Defaults_AreDisabledWithJsDelivrDatabaseUrl()
    {
        using ServiceProvider provider = BuildProvider([]);

        NodeEnrichmentOptions options = provider
            .GetRequiredService<IOptions<NodeEnrichmentOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.False);
            Assert.That(options.DatabasePath, Is.Empty);
            Assert.That(
                options.DatabaseUrl,
                Is.EqualTo(NodeEnrichmentOptions.DefaultDatabaseUrl));
        });
    }

    [Test]
    public void NestedConfiguration_BindsAllNodeEnrichmentSettings()
    {
        var values = new Dictionary<string, string?>
        {
            ["NodeEnrichment:Enabled"] = "true",
            ["NodeEnrichment:DatabasePath"] = "/data/city.mmdb",
            ["NodeEnrichment:DatabaseUrl"] = "https://example.test/city.mmdb.gz"
        };
        using ServiceProvider provider = BuildProvider(values);

        NodeEnrichmentOptions options = provider
            .GetRequiredService<IOptions<NodeEnrichmentOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.DatabasePath, Is.EqualTo("/data/city.mmdb"));
            Assert.That(
                options.DatabaseUrl,
                Is.EqualTo("https://example.test/city.mmdb.gz"));
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
