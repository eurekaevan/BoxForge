using System.Net;
using System.Text;
using BoxForge.Configuration;
using BoxForge.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class Ip2LocationCityClientTests
{
    [Test]
    public async Task AuthenticatedRequestReadsCityAndCachesSameIp()
    {
        const string apiKey = "secret-key";
        Uri? requestUri = null;
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        var handler = new StubHttpHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            authorizationScheme = request.Headers.Authorization?.Scheme;
            authorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(JsonResponse("{\"city_name\":\"Tokyo\"}"));
        });
        var client = CreateClient(handler, apiKey);
        var address = IPAddress.Parse("203.0.113.1");

        Task<string?> firstTask = client.FindCityAsync(address);
        Task<string?> secondTask = client.FindCityAsync(address);
        string?[] cities = await Task.WhenAll(firstTask, secondTask);

        Assert.Multiple(() =>
        {
            Assert.That(cities, Is.EqualTo(new[] { "Tokyo", "Tokyo" }));
            Assert.That(handler.CallCount, Is.EqualTo(1));
            Assert.That(requestUri!.Host, Is.EqualTo("api.ip2location.io"));
            Assert.That(requestUri.Query, Does.Not.Contain(apiKey));
            Assert.That(
                Uri.UnescapeDataString(requestUri.Query),
                Does.Not.Contain("key=").And.Contain("ip=203.0.113.1"));
            Assert.That(authorizationScheme, Is.EqualTo("Bearer"));
            Assert.That(authorizationParameter, Is.EqualTo(apiKey));
        });
    }

    [Test]
    public async Task EmptyApiKeyOmitsKeyParameter()
    {
        Uri? requestUri = null;
        bool hasAuthorization = true;
        var handler = new StubHttpHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            hasAuthorization = request.Headers.Authorization != null;
            return Task.FromResult(JsonResponse("{\"city_name\":\"Tokyo\"}"));
        });
        var client = CreateClient(handler, "  ");

        string? city = await client.FindCityAsync(
            IPAddress.Parse("2001:db8::1"));

        Assert.Multiple(() =>
        {
            Assert.That(city, Is.EqualTo("Tokyo"));
            Assert.That(requestUri!.Query, Does.Not.Contain("key="));
            Assert.That(hasAuthorization, Is.False);
            Assert.That(
                Uri.UnescapeDataString(requestUri.Query),
                Does.Contain("ip=2001:db8::1"));
        });
    }

    [Test]
    public async Task RequestsUseAtMostFourConcurrentSlots()
    {
        int active = 0;
        int maximum = 0;
        var handler = new StubHttpHandler(async (_, cancellationToken) =>
        {
            int current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            try
            {
                await Task.Delay(30, cancellationToken);
                return JsonResponse("{\"city_name\":\"Tokyo\"}");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var client = CreateClient(handler, "");
        Task<string?>[] requests = [.. Enumerable.Range(1, 12).Select(
            lastOctet => client.FindCityAsync(
                IPAddress.Parse($"203.0.113.{lastOctet}")))];

        await Task.WhenAll(requests);

        Assert.Multiple(() =>
        {
            Assert.That(handler.CallCount, Is.EqualTo(12));
            Assert.That(maximum, Is.EqualTo(4));
        });
    }

    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task FailedRequestReturnsNullAndDoesNotLogApiKeyOrUrl(
        HttpStatusCode statusCode)
    {
        const string apiKey = "must-not-leak";
        var logger = new RecordingLogger<Ip2LocationCityClient>();
        var handler = new StubHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode)));
        var client = CreateClient(handler, apiKey, logger);

        string? city = await client.FindCityAsync(
            IPAddress.Parse("203.0.113.1"));

        Assert.Multiple(() =>
        {
            Assert.That(city, Is.Null);
            Assert.That(logger.Levels, Does.Contain(LogLevel.Warning));
            Assert.That(logger.Exceptions, Is.Empty);
            Assert.That(logger.Messages, Has.None.Contains(apiKey));
            Assert.That(
                logger.Messages,
                Has.None.Contains("https://api.ip2location.io"));
        });
    }

    [Test]
    public async Task TimeoutReturnsNullAndLogsSafeWarning()
    {
        var logger = new RecordingLogger<Ip2LocationCityClient>();
        var handler = new StubHttpHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
        var client = CreateClient(handler, "must-not-leak", logger);

        string? city = await client.FindCityAsync(
            IPAddress.Parse("203.0.113.1"));

        Assert.Multiple(() =>
        {
            Assert.That(city, Is.Null);
            Assert.That(logger.Levels, Does.Contain(LogLevel.Warning));
            Assert.That(logger.Messages, Has.None.Contains("must-not-leak"));
        });
    }

    private static Ip2LocationCityClient CreateClient(
        HttpMessageHandler handler,
        string apiKey,
        ILogger<Ip2LocationCityClient>? logger = null) =>
        new(
            Options.Create(new NodeEnrichmentOptions
            {
                Ip2LocationApiKey = apiKey
            }),
            new HttpClient(handler),
            logger ?? new RecordingLogger<Ip2LocationCityClient>());

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref maximum);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, current) != current);
    }
}
