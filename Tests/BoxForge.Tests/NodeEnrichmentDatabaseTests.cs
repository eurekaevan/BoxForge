using System.IO.Compression;
using System.Net;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeEnrichmentDatabaseTests
{
    [Test]
    public void LocalDatabaseTakesPriorityWithoutHttpRequest()
    {
        string temporaryFile = Path.GetTempFileName();
        try
        {
            using var httpClient = new HttpClient(new ThrowingHttpHandler());
            using var source = CreateSource(
                new NodeEnrichmentOptions
                {
                    DatabasePath = temporaryFile,
                    DatabaseUrl = "https://example.test/database.mmdb.gz"
                },
                httpClient);

            string result = source.GetDatabasePath();

            Assert.That(result, Is.EqualTo(Path.GetFullPath(temporaryFile)));
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    [Test]
    public void DownloadedDatabaseIsDecompressedAndReused()
    {
        byte[] expectedContent = "test-mmdb-content"u8.ToArray();
        var handler = new StubHttpHandler(Compress(expectedContent));
        using var httpClient = new HttpClient(handler);
        var source = CreateSource(
            new NodeEnrichmentOptions
            {
                DatabaseUrl = "https://example.test/database.mmdb.gz"
            },
            httpClient);

        string firstPath = source.GetDatabasePath();
        string secondPath = source.GetDatabasePath();
        string temporaryDirectory = Path.GetDirectoryName(firstPath)!;

        Assert.Multiple(() =>
        {
            Assert.That(secondPath, Is.EqualTo(firstPath));
            Assert.That(File.ReadAllBytes(firstPath), Is.EqualTo(expectedContent));
            Assert.That(handler.CallCount, Is.EqualTo(1));
        });

        source.Dispose();
        Assert.That(Directory.Exists(temporaryDirectory), Is.False);
    }

    [Test]
    public void MissingLocalDatabaseFallsBackToDownload()
    {
        byte[] expectedContent = "downloaded-mmdb"u8.ToArray();
        var handler = new StubHttpHandler(Compress(expectedContent));
        using var httpClient = new HttpClient(handler);
        using var source = CreateSource(
            new NodeEnrichmentOptions
            {
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    $"missing-{Guid.NewGuid():N}.mmdb"),
                DatabaseUrl = "https://example.test/database.mmdb.gz"
            },
            httpClient);

        string result = source.GetDatabasePath();

        Assert.That(File.ReadAllBytes(result), Is.EqualTo(expectedContent));
        Assert.That(handler.CallCount, Is.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void DownloadOrDecompressionFailure_IsNonFatalForTagEnrichment(
        bool failDownload)
    {
        var handler = new StubHttpHandler(
            "not-gzip"u8.ToArray(),
            failDownload
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        using var source = CreateSource(
            new NodeEnrichmentOptions
            {
                DatabaseUrl = "https://example.test/database.mmdb.gz"
            },
            httpClient);
        using var database = new GeoLite2CityDatabase(
            source,
            new MaxMindGeoLite2CityReaderFactory());
        var logger = new RecordingLogger<NodeCityTagEnricher>();
        var enricher = new NodeCityTagEnricher(
            new ThrowingAddressResolver(),
            database,
            logger);
        var outbound = new ShadowsocksOutbound
        {
            Tag = "node",
            Server = "203.0.113.1",
            ServerPort = 443,
            Method = "aes-128-gcm",
            Password = "secret"
        };
        var catalog = new BoxForge.Builders.NodeCatalog(
            [outbound],
            [outbound.Tag],
            []);

        BoxForge.Builders.NodeCatalog result = enricher.Enrich(catalog);

        Assert.That(result.Outbounds[0].Tag, Is.EqualTo("node"));
        Assert.That(logger.Levels, Does.Contain(LogLevel.Warning));
        Assert.That(handler.CallCount, Is.EqualTo(1));
    }

    [Test]
    public void GeoLite2Database_ReusesOneReader()
    {
        var source = new StubDatabaseSource("database.mmdb");
        var factory = new StubReaderFactory("Tokyo");
        using var database = new GeoLite2CityDatabase(source, factory);

        string? first = database.FindEnglishCity(IPAddress.Parse("203.0.113.1"));
        string? second = database.FindEnglishCity(IPAddress.Parse("2001:db8::1"));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("Tokyo"));
            Assert.That(second, Is.EqualTo("Tokyo"));
            Assert.That(source.CallCount, Is.EqualTo(1));
            Assert.That(factory.OpenCount, Is.EqualTo(1));
            Assert.That(factory.Reader.LookupCount, Is.EqualTo(2));
        });
    }

    private static NodeEnrichmentDatabaseSource CreateSource(
        NodeEnrichmentOptions options,
        HttpClient httpClient) =>
        new(
            Options.Create(options),
            httpClient,
            NullLogger<NodeEnrichmentDatabaseSource>.Instance);

    private static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(
            output,
            CompressionLevel.SmallestSize,
            leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }
}
