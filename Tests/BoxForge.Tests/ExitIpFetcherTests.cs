using System.Net;
using System.Net.Sockets;
using System.Text;
using BoxForge.Services;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ExitIpFetcherTests
{
    [Test]
    public async Task FallsBackToUniversalIpifyAfterIpv4TransportFailure()
    {
        var requestedHosts = new List<string>();
        var handler = new StubHttpHandler((request, _) =>
        {
            requestedHosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "api.ipify.org")
            {
                return Task.FromException<HttpResponseMessage>(
                    CreateHttpFailure(
                        HttpRequestError.ConnectionError,
                        SocketError.ConnectionReset,
                        "must-not-leak"));
            }

            return Task.FromResult(TextResponse("2001:db8::1"));
        });
        var factory = new StubExitIpHttpClientFactory(handler);
        var fetcher = new ExitIpFetcher(factory);

        IPAddress? result = await fetcher.FetchAsync(1080);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(IPAddress.Parse("2001:db8::1")));
            Assert.That(
                requestedHosts,
                Is.EqualTo(new[] { "api.ipify.org", "api64.ipify.org" }));
            Assert.That(factory.SocksPorts, Is.EqualTo(new[] { 1080 }));
        });
    }

    [Test]
    public void AllFailuresExposeOnlySafeStructuredReasons()
    {
        const string secret = "must-not-leak";
        var handler = new StubHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.ipify.org")
            {
                return Task.FromException<HttpResponseMessage>(
                    CreateHttpFailure(
                        HttpRequestError.SecureConnectionError,
                        SocketError.ConnectionAborted,
                        secret));
            }

            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable));
        });
        var fetcher = new ExitIpFetcher(
            new StubExitIpHttpClientFactory(handler));

        var exception = Assert.ThrowsAsync<ExitIpFetchException>(
            () => fetcher.FetchAsync(1080));

        Assert.Multiple(() =>
        {
            Assert.That(
                exception!.Reason,
                Is.EqualTo(
                    "ipv4:SecureConnectionError/ConnectionAborted,"
                    + "universal:http-503"));
            Assert.That(exception.Reason, Does.Not.Contain(secret));
            Assert.That(exception.InnerException, Is.Null);
        });
    }

    [Test]
    public void InvalidResponsesAreClassifiedWithoutReturningAnAddress()
    {
        var handler = new StubHttpHandler((_, _) =>
            Task.FromResult(TextResponse("not-an-ip")));
        var fetcher = new ExitIpFetcher(
            new StubExitIpHttpClientFactory(handler));

        var exception = Assert.ThrowsAsync<ExitIpFetchException>(
            () => fetcher.FetchAsync(1080));

        Assert.That(
            exception!.Reason,
            Is.EqualTo(
                "ipv4:invalid-response,universal:invalid-response"));
    }

    private static HttpRequestException CreateHttpFailure(
        HttpRequestError error,
        SocketError socketError,
        string message) =>
        new(
            error,
            message,
            new SocketException((int)socketError),
            statusCode: null);

    private static HttpResponseMessage TextResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain")
        };
}
