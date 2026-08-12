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
    public async Task UsesDetectedExitInsteadOfNodeServerAndMergesFixedOrder()
    {
        var serverAddress = IPAddress.Parse("198.51.100.10");
        var exitAddress = IPAddress.Parse("203.0.113.1");
        var detector = new StubExitIpDetector([exitAddress]);
        var enricher = CreateEnricher(
            detector,
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = " Tokyo (Tokyo Prefecture) "
            }),
            new StubIp2LocationCityClient(
                new Dictionary<IPAddress, string?>
                {
                    [exitAddress] = "Osaka"
                }));

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog(CreateOutbound("node", serverAddress.ToString())));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Tokyo\\Osaka"));
            Assert.That(
                result.Outbounds[0].Server,
                Is.EqualTo(serverAddress.ToString()));
            Assert.That(result.Names, Is.EqualTo(new[] { "node>Tokyo\\Osaka" }));
            Assert.That(detector.CallCount, Is.EqualTo(1));
            Assert.That(
                detector.Outbounds[0].Server,
                Is.EqualTo(serverAddress.ToString()));
        });
    }

    [Test]
    public async Task EqualCitiesUseDbIpCasingAndOneSuffix()
    {
        var exitAddress = IPAddress.Parse("203.0.113.1");
        var enricher = CreateEnricher(
            new StubExitIpDetector([exitAddress]),
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = " Tokyo (Japan) "
            }),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = "tokyo"
            }));

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog(CreateOutbound("node", "node.example")));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Tokyo"));
    }

    [TestCase(SourceFailure.DbIp, "node>Tokyo")]
    [TestCase(SourceFailure.Ip2Location, "node>Tokyo")]
    [TestCase(SourceFailure.Both, "node")]
    public async Task SourceFailureDoesNotAffectOtherSource(
        SourceFailure failure,
        string expectedTag)
    {
        var exitAddress = IPAddress.Parse("203.0.113.1");
        IDbIpCityDatabase dbIp = failure is SourceFailure.DbIp or SourceFailure.Both
            ? new ThrowingDbIpCityDatabase()
            : new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = "Tokyo"
            });
        IIp2LocationCityClient ip2Location = failure is
            SourceFailure.Ip2Location or SourceFailure.Both
            ? new ThrowingIp2LocationCityClient()
            : new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = "Tokyo"
            });
        var logger = new RecordingLogger<NodeCityTagEnricher>();
        var enricher = CreateEnricher(
            new StubExitIpDetector([exitAddress]),
            dbIp,
            ip2Location,
            logger: logger);

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog(CreateOutbound("node", "node.example")));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo(expectedTag));
        Assert.That(logger.Levels, Does.Contain(LogLevel.Warning));
    }

    [Test]
    public async Task DbIpInitializationFailureUsesIp2LocationResult()
    {
        var exitAddress = IPAddress.Parse("203.0.113.1");
        var logger = new RecordingLogger<NodeCityTagEnricher>();
        var enricher = CreateEnricher(
            new StubExitIpDetector([exitAddress]),
            new ThrowingInitializingDbIpCityDatabase(),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = "Tokyo"
            }),
            logger: logger);

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog(CreateOutbound("node", "node.example")));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node>Tokyo"));
            Assert.That(
                logger.Messages,
                Has.Some.Contains("DB-IP City Lite 数据库初始化失败"));
        });
    }

    [Test]
    public async Task MissingCitiesKeepOriginalTag()
    {
        var exitAddress = IPAddress.Parse("203.0.113.1");
        var enricher = CreateEnricher(
            new StubExitIpDetector([exitAddress]),
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = " - "
            }),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = null
            }));

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog(CreateOutbound("node", "node.example")));

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node"));
    }

    [Test]
    public async Task SameExitIsProbedPerNodeButGeolocationIsCachedAndSequential()
    {
        var exitAddress = IPAddress.Parse("203.0.113.1");
        var detector = new StubExitIpDetector(
            [exitAddress, exitAddress],
            TimeSpan.FromMilliseconds(10));
        var dbIp = new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
        {
            [exitAddress] = "Tokyo"
        });
        var ip2Location = new StubIp2LocationCityClient(
            new Dictionary<IPAddress, string?>
            {
                [exitAddress] = "Tokyo"
            });
        var enricher = CreateEnricher(detector, dbIp, ip2Location);

        NodeCatalog result = await enricher.EnrichAsync(CreateCatalog(
            CreateOutbound("first", "first.example"),
            CreateOutbound("second", "second.example")));

        Assert.Multiple(() =>
        {
            Assert.That(detector.CallCount, Is.EqualTo(2));
            Assert.That(detector.MaximumConcurrency, Is.EqualTo(1));
            Assert.That(dbIp.InitializeCount, Is.EqualTo(1));
            Assert.That(dbIp.LookupCount, Is.EqualTo(1));
            Assert.That(ip2Location.CallCount, Is.EqualTo(1));
            Assert.That(
                result.Names,
                Is.EqualTo(new[] { "first>Tokyo", "second>Tokyo" }));
        });
    }

    [Test]
    public async Task FinalTagsFlowIntoOutboundAndEveryGeneratedSelector()
    {
        var exitAddress = IPAddress.Parse("203.0.113.1");
        var enricher = CreateEnricher(
            new StubExitIpDetector([exitAddress]),
            new StubDbIpCityDatabase(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = "Tokyo"
            }),
            new StubIp2LocationCityClient(new Dictionary<IPAddress, string?>
            {
                [exitAddress] = "Tokyo"
            }));
        NodeCatalog nodes = await enricher.EnrichAsync(
            CreateCatalog(CreateOutbound("node", "node.example")));

        ProfilePlan plan = new ProfilePlanner(Options.Create(new SingboxOptions()))
            .Plan(nodes);
        IEnumerable<string> selectorTargets = plan.RegionOutbounds
            .Concat(plan.ServiceOutbounds)
            .Append(plan.MainOutbound)
            .SelectMany(outbound => outbound.Outbounds);

        Assert.Multiple(() =>
        {
            Assert.That(nodes.Outbounds[0].Tag, Is.EqualTo("node>Tokyo"));
            Assert.That(selectorTargets, Does.Contain("node>Tokyo"));
            Assert.That(selectorTargets, Does.Not.Contain("node"));
        });
    }

    [Test]
    public async Task DisabledEnrichmentDoesNotProbeOrQuery()
    {
        NodeCatalog catalog = CreateCatalog(
            CreateOutbound("node", "node.example"));
        var enricher = CreateEnricher(
            new ThrowingExitIpDetector(),
            new ThrowingDbIpCityDatabase(),
            new ThrowingIp2LocationCityClient(),
            enabled: false);

        NodeCatalog result = await enricher.EnrichAsync(catalog);

        Assert.That(result, Is.SameAs(catalog));
    }

    [Test]
    public async Task FailedExitDetectionKeepsOriginalTagWithoutGeoLookup()
    {
        var dbIp = new StubDbIpCityDatabase(
            new Dictionary<IPAddress, string?>());
        var ip2Location = new StubIp2LocationCityClient(
            new Dictionary<IPAddress, string?>());
        var enricher = CreateEnricher(
            new StubExitIpDetector([null]),
            dbIp,
            ip2Location);

        NodeCatalog result = await enricher.EnrichAsync(
            CreateCatalog(CreateOutbound("node", "node.example")));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node"));
            Assert.That(dbIp.InitializeCount, Is.Zero);
            Assert.That(ip2Location.CallCount, Is.Zero);
        });
    }

    private static NodeCatalog CreateCatalog(params ProxyOutbound[] outbounds) =>
        new(
            outbounds,
            [.. outbounds.Select(outbound => outbound.Tag)],
            [.. outbounds
                .Select(outbound => outbound.Server)
                .Where(server => !IPAddress.TryParse(server, out _))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)]);

    private static ShadowsocksOutbound CreateOutbound(string tag, string server) =>
        new()
        {
            Tag = tag,
            Server = server,
            ServerPort = 443,
            Method = "aes-128-gcm",
            Password = "secret"
        };

    private static NodeCityTagEnricher CreateEnricher(
        IExitIpDetector exitIpDetector,
        IDbIpCityDatabase dbIp,
        IIp2LocationCityClient ip2Location,
        bool enabled = true,
        ILogger<NodeCityTagEnricher>? logger = null) =>
        new(
            Options.Create(new NodeEnrichmentOptions
            {
                Enabled = enabled,
                Mode = NodeEnrichmentMode.Exit
            }),
            exitIpDetector,
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
