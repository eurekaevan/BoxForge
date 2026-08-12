using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeEnrichmentOptionsTests
{
    [Test]
    public void DefaultsEnableExitEnrichmentAndUseExpectedDependencies()
    {
        using ServiceProvider provider = BuildProvider([]);

        NodeEnrichmentOptions options = provider
            .GetRequiredService<IOptions<NodeEnrichmentOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.Mode, Is.EqualTo(NodeEnrichmentMode.Exit));
            Assert.That(options.Ip2LocationApiKey, Is.Empty);
            Assert.That(
                options.DbIpDatabaseUrl,
                Is.EqualTo(NodeEnrichmentOptions.DefaultDbIpDatabaseUrl));
            Assert.That(options.SingBoxPath, Is.EqualTo("sing-box"));
        });
    }

    [Test]
    public void NestedConfigurationBindsAllNodeEnrichmentSettings()
    {
        var values = new Dictionary<string, string?>
        {
            ["NodeEnrichment:Enabled"] = "true",
            ["NodeEnrichment:Mode"] = "exit",
            ["NodeEnrichment:Ip2LocationApiKey"] = "api-secret",
            ["NodeEnrichment:DbIpDatabaseUrl"] =
                "https://example.test/dbip.mmdb.gz",
            ["NodeEnrichment:SingBoxPath"] = "/opt/sing-box"
        };
        using ServiceProvider provider = BuildProvider(values);

        NodeEnrichmentOptions options = provider
            .GetRequiredService<IOptions<NodeEnrichmentOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.Mode, Is.EqualTo(NodeEnrichmentMode.Exit));
            Assert.That(options.Ip2LocationApiKey, Is.EqualTo("api-secret"));
            Assert.That(
                options.DbIpDatabaseUrl,
                Is.EqualTo("https://example.test/dbip.mmdb.gz"));
            Assert.That(options.SingBoxPath, Is.EqualTo("/opt/sing-box"));
        });
    }

    [TestCase("Other", "sing-box")]
    [TestCase("Exit", "  ")]
    public void EnabledConfigurationRejectsUnsupportedModeOrEmptyPath(
        string mode,
        string singBoxPath)
    {
        var values = new Dictionary<string, string?>
        {
            ["NodeEnrichment:Enabled"] = "true",
            ["NodeEnrichment:Mode"] = mode,
            ["NodeEnrichment:SingBoxPath"] = singBoxPath
        };
        using ServiceProvider provider = BuildProvider(values);

        Assert.Throws<OptionsValidationException>(() => _ = provider
            .GetRequiredService<IOptions<NodeEnrichmentOptions>>()
            .Value);
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
