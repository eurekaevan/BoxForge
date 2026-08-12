using System.IO.Compression;
using System.Net;
using BoxForge.Configuration;
using BoxForge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class DbIpCityDatabaseTests
{
    [Test]
    public async Task DatabaseIsDownloadedDecompressedAndReusedOnce()
    {
        byte[] expectedContent = "test-mmdb-content"u8.ToArray();
        var handler = new StubHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Compress(expectedContent))
            }));
        using var httpClient = new HttpClient(handler);
        var source = new DbIpDatabaseSource(
            Options.Create(new NodeEnrichmentOptions
            {
                DbIpDatabaseUrl = "https://example.test/database.mmdb.gz"
            }),
            httpClient,
            NullLogger<DbIpDatabaseSource>.Instance);

        string firstPath = await source.GetDatabasePathAsync();
        string secondPath = await source.GetDatabasePathAsync();
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
    public async Task DatabaseReaderIsOpenedOnceForAllAddresses()
    {
        var source = new StubDbIpDatabaseSource("database.mmdb");
        var factory = new StubDbIpReaderFactory("Tokyo");
        using var database = new DbIpCityDatabase(source, factory);

        await database.InitializeAsync();
        await database.InitializeAsync();
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

    [Test]
    public void InvalidDatabaseUrlDoesNotExposeConfiguredValueInException()
    {
        const string configuredValue = "secret-invalid-url";
        using var httpClient = new HttpClient();
        using var source = new DbIpDatabaseSource(
            Options.Create(new NodeEnrichmentOptions
            {
                DbIpDatabaseUrl = configuredValue
            }),
            httpClient,
            NullLogger<DbIpDatabaseSource>.Instance);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await source.GetDatabasePathAsync());

        Assert.That(exception!.ToString(), Does.Not.Contain(configuredValue));
    }

    [Test]
    public void DatabaseDownloadPropagatesCancellation()
    {
        var handler = new StubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        using var source = new DbIpDatabaseSource(
            Options.Create(new NodeEnrichmentOptions
            {
                DbIpDatabaseUrl = "https://example.test/database.mmdb.gz"
            }),
            httpClient,
            NullLogger<DbIpDatabaseSource>.Instance);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.CatchAsync<OperationCanceledException>(
            async () => await source.GetDatabasePathAsync(
                cancellationSource.Token));
    }

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
