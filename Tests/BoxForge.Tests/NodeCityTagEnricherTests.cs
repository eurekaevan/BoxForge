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
    public async Task MultipleAddressesAreDeduplicatedPerSourceAndMergedInFixedOrder()
    {
        var ipv4 = IPAddress.Parse("203.0.113.1");
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var resolver = new StubAddressResolver([ipv6, ipv4, ipv4]);
        var dbIp = new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
        {
            [ipv4] = " Tokyo (Tokyo Prefecture) ",
            [ipv6] = "Singapore（Central）"
        });
        var ip2Location = new StubIp2LocationCityClient(
            new Dictionary<IPAddress, string?>
            {
                [ipv4] = "tokyo",
                [ipv6] = "Osaka"
            });
        var enricher = CreateEnricher(resolver, dbIp, ip2Location);

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog("node", "example.test"));

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Outbounds[0].Tag,
                Is.EqualTo("node>Tokyo/Singapore\\tokyo/Osaka"));
            Assert.That(result.Outbounds[0].Server, Is.EqualTo("example.test"));
            Assert.That(
                result.Names,
                Is.EqualTo(new[] { "node>Tokyo/Singapore\\tokyo/Osaka" }));
            Assert.That(resolver.CallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task EqualCitiesUseDbIpCasingAndOneSuffix()
    {
        var address = IPAddress.Parse("203.0.113.1");
        var enricher = CreateEnricher(
            new ThrowingAddressResolver(),
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [address] = " Tokyo (Japan) "
            }),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [address] = "tokyo"
            }));

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog("node", address.ToString()));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Tokyo"));
        Assert.That(result.Outbounds[0].Server, Is.EqualTo(address.ToString()));
    }

    [TestCase(SourceFailure.DbIp, "node>Tokyo")]
    [TestCase(SourceFailure.Ip2Location, "node>Tokyo")]
    [TestCase(SourceFailure.Both, "node")]
    public async Task SourceFailureDoesNotAffectOtherSource(
        SourceFailure failure,
        string expectedTag)
    {
        var address = IPAddress.Parse("203.0.113.1");
        IDbIpCityDatabase dbIp = failure is SourceFailure.DbIp or SourceFailure.Both
            ? new ThrowingDbIpCityDatabase()
            : new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [address] = "Tokyo"
            });
        IIp2LocationCityClient ip2Location = failure is
            SourceFailure.Ip2Location or SourceFailure.Both
            ? new ThrowingIp2LocationCityClient()
            : new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [address] = "Tokyo"
            });
        var logger = new RecordingLogger<NodeCityTagEnricher>();
        var enricher = CreateEnricher(
            new ThrowingAddressResolver(),
            dbIp,
            ip2Location,
            logger: logger);

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog("node", address.ToString()));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo(expectedTag));
        Assert.That(logger.Levels, Does.Contain(LogLevel.Warning));
    }

    [Test]
    public async Task DbIpInitializationFailureUsesIp2LocationResult()
    {
        var address = IPAddress.Parse("203.0.113.1");
        var logger = new RecordingLogger<NodeCityTagEnricher>();
        var enricher = CreateEnricher(
            new ThrowingAddressResolver(),
            new ThrowingInitializingDbIpCityDatabase(),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [address] = "Tokyo"
            }),
            logger: logger);

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog("node", address.ToString()));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Tokyo"));
            Assert.That(logger.Levels, Does.Contain(LogLevel.Warning));
            Assert.That(
                logger.Messages,
                Has.Some.Contains("DB-IP City Lite 数据库初始化失败"));
        });
    }

    [Test]
    public async Task MissingValuesUseAvailableSourceAndIgnoreDash()
    {
        var first = IPAddress.Parse("203.0.113.1");
        var second = IPAddress.Parse("203.0.113.2");
        var enricher = CreateEnricher(
            new StubAddressResolver([first, second]),
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [first] = "-",
                [second] = "  "
            }),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [first] = " Singapore ",
                [second] = "singapore"
            }));

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog("node", "example.test"));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Singapore"));
    }

    [Test]
    public async Task BothSourcesWithoutCitiesKeepOriginalTag()
    {
        var address = IPAddress.Parse("203.0.113.1");
        var enricher = CreateEnricher(
            new ThrowingAddressResolver(),
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [address] = " - "
            }),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [address] = null
            }));

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog("node", address.ToString()));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node"));
    }

    [Test]
    public async Task FinalTagsFlowIntoSelectorsWithoutChangingServers()
    {
        var address = IPAddress.Parse("203.0.113.1");
        var enricher = CreateEnricher(
            new ThrowingAddressResolver(),
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [address] = "Tokyo"
            }),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [address] = "Tokyo"
            }));
        NodeCatalog nodes = await enricher.EnrichAsync(
            CreateCatalog("node", address.ToString()));

        var plan = new ProfilePlanner(Options.Create(new SingboxOptions()))
            .Plan(nodes);

        Assert.Multiple(() =>
        {
            Assert.That(nodes.Outbounds[0].Server, Is.EqualTo(address.ToString()));
            Assert.That(plan.MainOutbound.Outbounds, Does.Contain("node>Tokyo"));
            Assert.That(plan.MainOutbound.Outbounds, Does.Not.Contain("node"));
            Assert.That(
                plan.ServiceOutbounds.SelectMany(outbound => outbound.Outbounds),
                Does.Contain("node>Tokyo"));
        });
    }

    [Test]
    public async Task DisabledEnrichmentDoesNotResolveOrQuery()
    {
        NodeCatalog catalog = CreateCatalog("node", "example.test");
        var enricher = CreateEnricher(
            new ThrowingAddressResolver(),
            new ThrowingDbIpCityDatabase(),
            new ThrowingIp2LocationCityClient(),
            enabled: false);

        NodeCatalog result = await enricher.EnrichAsync(catalog);

        Assert.That(result, Is.SameAs(catalog));
    }

    [Test]
    public async Task DnsFailureKeepsOriginalTagAndDoesNotFailGeneration()
    {
        var logger = new RecordingLogger<NodeCityTagEnricher>();
        var enricher = CreateEnricher(
            new ThrowingAddressResolver(),
            new ThrowingDbIpCityDatabase(),
            new ThrowingIp2LocationCityClient(),
            logger: logger);

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog("node", "example.test"));

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
        IHostAddressResolver resolver,
        IDbIpCityDatabase dbIp,
        IIp2LocationCityClient ip2Location,
        bool enabled = true,
        ILogger<NodeCityTagEnricher>? logger = null) =>
        new(
            Options.Create(new NodeEnrichmentOptions { Enabled = enabled }),
            resolver,
            dbIp,
            ip2Location,
            logger ?? NullLogger<NodeCityTagEnricher>.Instance);

    public enum SourceFailure
    {
        DbIp,
        Ip2Location,
        Both
    }
}
