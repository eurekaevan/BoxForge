using System.Net;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeCityTagEnricherTests
{
    [Test]
    public void Disabled_DoesNotResolveDnsOrAccessDatabase()
    {
        var catalog = CreateCatalog("node", "example.test");
        var enricher = CreateEnricher(
            enabled: false,
            new ThrowingAddressResolver(),
            new ThrowingCityDatabase());

        NodeCatalog result = enricher.Enrich(catalog);

        Assert.That(result, Is.SameAs(catalog));
    }

    [Test]
    public void DomainAddresses_ProduceStableDeduplicatedTagAndSelectorReferences()
    {
        var ipv4 = IPAddress.Parse("203.0.113.1");
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var resolver = new StubAddressResolver([ipv6, ipv4, ipv4]);
        var database = new StubCityDatabase(new Dictionary<IPAddress, string?>
        {
            [ipv4] = "Tokyo",
            [ipv6] = "Singapore"
        });
        var catalog = CreateCatalog("node", "example.test");
        var enricher = CreateEnricher(true, resolver, database);

        NodeCatalog result = enricher.Enrich(catalog);

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Singapore/Tokyo"));
        Assert.That(result.Outbounds[0].Server, Is.EqualTo("example.test"));
        Assert.That(result.Names, Is.EqualTo(new[] { "node>Singapore/Tokyo" }));
        Assert.That(resolver.CallCount, Is.EqualTo(1));

        var plan = new ProfilePlanner(Options.Create(new SingboxOptions()))
            .Plan(result);
        Assert.That(
            plan.MainOutbound.Outbounds,
            Does.Contain("node>Singapore/Tokyo"));
        Assert.That(plan.MainOutbound.Outbounds, Does.Not.Contain("node"));
        Assert.That(
            plan.ServiceOutbounds.SelectMany(outbound => outbound.Outbounds),
            Does.Contain("node>Singapore/Tokyo"));
        Assert.That(
            plan.ServiceOutbounds.SelectMany(outbound => outbound.Outbounds),
            Does.Not.Contain("node"));
    }

    [Test]
    public void IpAddress_BypassesDnsAndPreservesServer()
    {
        var address = IPAddress.Parse("203.0.113.1");
        var database = new StubCityDatabase(new Dictionary<IPAddress, string?>
        {
            [address] = "Tokyo"
        });
        var catalog = CreateCatalog("node", address.ToString());
        var enricher = CreateEnricher(
            enabled: true,
            new ThrowingAddressResolver(),
            database);

        NodeCatalog result = enricher.Enrich(catalog);

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Tokyo"));
        Assert.That(result.Outbounds[0].Server, Is.EqualTo(address.ToString()));
    }

    [TestCase(FailureKind.Dns)]
    [TestCase(FailureKind.Database)]
    [TestCase(FailureKind.MissingCity)]
    public void EnrichmentFailure_KeepsOriginalTagAndLogsWarning(
        FailureKind failureKind)
    {
        var address = IPAddress.Parse("203.0.113.1");
        IHostAddressResolver resolver = failureKind == FailureKind.Dns
            ? new ThrowingAddressResolver()
            : new StubAddressResolver([address]);
        ICityDatabase database = failureKind switch
        {
            FailureKind.Database => new ThrowingCityDatabase(),
            FailureKind.MissingCity => new StubCityDatabase(
                new Dictionary<IPAddress, string?> { [address] = null }),
            _ => new StubCityDatabase(
                new Dictionary<IPAddress, string?> { [address] = "Tokyo" })
        };
        var logger = new RecordingLogger<NodeCityTagEnricher>();
        var enricher = CreateEnricher(true, resolver, database, logger);

        NodeCatalog result = enricher.Enrich(CreateCatalog("node", "example.test"));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node"));
        Assert.That(logger.Levels, Does.Contain(LogLevel.Warning));
    }

    private static NodeCatalog CreateCatalog(string tag, string server)
    {
        var outbound = new ShadowsocksOutbound
        {
            Tag = tag,
            Server = server,
            ServerPort = 443,
            Method = "aes-128-gcm",
            Password = "secret"
        };
        return new NodeCatalog(
            [outbound],
            [outbound.Tag],
            IPAddress.TryParse(server, out _) ? [] : [server]);
    }

    private static NodeCityTagEnricher CreateEnricher(
        bool enabled,
        IHostAddressResolver resolver,
        ICityDatabase database,
        ILogger<NodeCityTagEnricher>? logger = null) =>
        new(
            Options.Create(new NodeEnrichmentOptions { Enabled = enabled }),
            resolver,
            database,
            logger ?? NullLogger<NodeCityTagEnricher>.Instance);

    public enum FailureKind
    {
        Dns,
        Database,
        MissingCity
    }
}
