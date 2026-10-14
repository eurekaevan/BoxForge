using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoxForge.Engine;
using BoxForge.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServerProgram = BoxForge.Server.Program;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ServerApiTests
{
    private const string ValidYaml = """
        proxies:
          - name: test
            type: ss
            server: node.example.com
            port: 443
            cipher: aes-128-gcm
            password: test-only
        """;

    private WebApplicationFactory<ServerProgram> factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        factory = new WebApplicationFactory<ServerProgram>();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        factory.Dispose();
    }

    [Test]
    public async Task HealthCheckReturnsOk()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/healthz");
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                body.RootElement.GetProperty("status").GetString(),
                Is.EqualTo("ok"));
        });
    }

    [Test]
    public async Task ConvertsSinglePlatform()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            "single",
            ValidYaml,
            ["Android"]);
        ApiConvertResponse result = (await response.Content
            .ReadFromJsonAsync<ApiConvertResponse>())!;
        ApiArtifact artifact = result.Artifacts.Single();
        string expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(artifact.Content)));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.Name, Is.EqualTo("single"));
            Assert.That(artifact.Platform, Is.EqualTo("Android"));
            Assert.That(
                artifact.Path,
                Is.EqualTo("single/Android/config.json"));
            Assert.That(artifact.Sha256, Is.EqualTo(expectedHash));
            Assert.That(artifact.Content, Does.Contain("\"outbounds\""));
        });
    }

    [Test]
    public async Task ConvertsThreePlatformsInFixedOrder()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            "all",
            ValidYaml,
            ["Windows", "Linux", "Android"]);
        ApiConvertResponse result = (await response.Content
            .ReadFromJsonAsync<ApiConvertResponse>())!;

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                result.Artifacts.Select(artifact => artifact.Platform),
                Is.EqualTo(new[] { "Android", "Linux", "Windows" }));
            Assert.That(
                result.Artifacts.Select(artifact => artifact.Path),
                Is.EqualTo(new[]
                {
                    "all/Android/config.json",
                    "all/Linux/config.json",
                    "all/Windows/config.json"
                }));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task KestrelHonorsPrefixedEnvironmentAndRejectsTruncatedMultipart(bool tailscaleEnabled)
    {
        // Keep environment changes in a child process; they must not race the in-process API fixtures.
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "BoxForge.Tests.runtimeconfig.json"));
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "BoxForge.Tests.deps.json"));
        startInfo.ArgumentList.Add(typeof(ServerProgram).Assembly.Location);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0");
        foreach (string key in startInfo.Environment.Keys.ToArray())
        {
            if (key.StartsWith("BOXFORGE_", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }

        startInfo.Environment["BOXFORGE_Tailscale__Enabled"] = tailscaleEnabled.ToString();
        startInfo.Environment["BOXFORGE_Tailscale__AndroidEnabled"] = tailscaleEnabled.ToString();
        startInfo.Environment["BOXFORGE_SingboxApi__Enabled"] = (!tailscaleEnabled).ToString();
        startInfo.Environment["Logging__LogLevel__Default"] = "Information";
        startInfo.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        using var process = Process.Start(startInfo)!;
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task<string>? outputTask = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            const string listeningPrefix = "Now listening on: ";
            string? line;
            Uri? address = null;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token)) != null)
            {
                int index = line.IndexOf(listeningPrefix, StringComparison.Ordinal);
                if (index >= 0)
                {
                    address = new Uri(line[(index + listeningPrefix.Length)..].Trim());
                    break;
                }
            }

            Assert.That(address, Is.Not.Null, "Kestrel did not start.");
            outputTask = process.StandardOutput.ReadToEndAsync();
            using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage converted = await PostConversionAsync(
                client, "environment", ValidYaml, ["Android", "Linux", "Windows"]);
            Assert.That(converted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            ApiConvertResponse result = (await converted.Content.ReadFromJsonAsync<ApiConvertResponse>())!;
            Assert.That(result.Artifacts, Has.Count.EqualTo(3));
            foreach (ApiArtifact artifact in result.Artifacts)
            {
                using JsonDocument config = JsonDocument.Parse(artifact.Content);
                bool hasTailscale = config.RootElement.TryGetProperty("endpoints", out JsonElement endpoints)
                    && endpoints.EnumerateArray().Any(endpoint => endpoint.GetProperty("type").GetString() == "tailscale");
                bool hasApi = config.RootElement.TryGetProperty("services", out JsonElement services)
                    && services.EnumerateArray().Any(service => service.GetProperty("type").GetString() == "api");
                Assert.Multiple(() =>
                {
                    Assert.That(hasTailscale, Is.EqualTo(tailscaleEnabled), artifact.Platform);
                    Assert.That(hasApi, Is.EqualTo(!tailscaleEnabled), artifact.Platform);
                });
            }

            using HttpResponseMessage cyclic = await PostConversionAsync(client, "cycle",
                ValidYaml + "\n    metadata: &cycle [*cycle]", ["Android"]);
            await AssertProblemAsync(cyclic, HttpStatusCode.UnprocessableEntity);

            using var content = new StringContent(
                "--test-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"input.yaml\"\r\n\r\nprivate-upload-content");
            content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
            content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", "test-boundary"));
            using HttpResponseMessage truncated = await client.PostAsync("/api/v1/export", content);
            await AssertProblemAsync(truncated, HttpStatusCode.BadRequest);
            Assert.That(await truncated.Content.ReadAsStringAsync(), Does.Not.Contain("private-upload-content"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            await errorTask;
            if (outputTask != null)
            {
                await outputTask;
            }
        }
    }

    [Test]
    public async Task ApiArtifactsMatchEngineByteForByte()
    {
        using HttpClient client = factory.CreateClient();
        var engine = factory.Services.GetRequiredService<IBoxForgeEngine>();
        var engineRequest = new ConversionRequest(
            "matching",
            ValidYaml,
            [TargetPlatform.Windows, TargetPlatform.Android]);
        ConversionBundle expected = await engine.ConvertAsync(engineRequest);

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            engineRequest.Name,
            engineRequest.ClashYaml,
            ["Windows", "Android"]);
        ApiConvertResponse actual = (await response.Content
            .ReadFromJsonAsync<ApiConvertResponse>())!;

        Assert.That(actual.Artifacts, Has.Count.EqualTo(expected.Artifacts.Count));
        for (var index = 0; index < expected.Artifacts.Count; index++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    actual.Artifacts[index].Platform,
                    Is.EqualTo(expected.Artifacts[index].Platform.ToString()));
                Assert.That(
                    actual.Artifacts[index].Content,
                    Is.EqualTo(expected.Artifacts[index].Content));
                Assert.That(
                    actual.Artifacts[index].Sha256,
                    Is.EqualTo(expected.Artifacts[index].Sha256));
            });
        }
    }

    [Test]
    public async Task RejectsEmptyRequiredValuesWithProblemDetails()
    {
        using HttpClient client = factory.CreateClient();
        object[] invalidRequests =
        [
            new { name = "", yaml = ValidYaml, platforms = new[] { "Android" } },
            new { name = "empty-yaml", yaml = "", platforms = new[] { "Android" } },
            new { name = "empty-platforms", yaml = ValidYaml, platforms = Array.Empty<string>() }
        ];

        foreach (object request in invalidRequests)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/v1/convert",
                request);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }
    }

    [TestCase(".")]
    [TestCase("..")]
    [TestCase("../escape")]
    [TestCase("..\\escape")]
    [TestCase("nested/name")]
    [TestCase("nested\\name")]
    [TestCase("line\nbreak")]
    [TestCase("bad:name")]
    [TestCase("bad<name")]
    [TestCase("bad>name")]
    [TestCase("bad\"name")]
    [TestCase("bad|name")]
    [TestCase("bad?name")]
    [TestCase("bad*name")]
    [TestCase("trailing.")]
    [TestCase("trailing ")]
    [TestCase("CON")]
    [TestCase("nul.txt")]
    [TestCase("AUX")]
    [TestCase("PRN.json")]
    [TestCase("COM1")]
    [TestCase("lpt9.log")]
    [TestCase("COM¹")]
    [TestCase("LPT².txt")]
    [TestCase("COM³")]
    [TestCase("CONIN$")]
    [TestCase("CONOUT$")]
    public async Task RejectsUnsafeConfigurationNames(string name)
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            name,
            ValidYaml,
            ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsConfigurationNameLongerThanOneHundredRunes()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            new string('a', 101),
            ValidYaml,
            ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [TestCase("0")]
    [TestCase("2")]
    [TestCase("Windows,Android")]
    [TestCase("Android, Android")]
    public async Task RejectsNonNamePlatformValues(string platform)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostConversionAsync(
            client, "invalid-platform", ValidYaml, [platform]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [TestCase(85, HttpStatusCode.OK)]
    [TestCase(86, HttpStatusCode.BadRequest)]
    public async Task ValidatesConfigurationNameUtf8ByteLimit(
        int runeCount,
        HttpStatusCode expectedStatus)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostConversionAsync(
            client, new string('中', runeCount), ValidYaml, ["android"]);

        Assert.That(response.StatusCode, Is.EqualTo(expectedStatus));
    }

    [TestCase("COM0")]
    [TestCase("LPT10")]
    [TestCase("console")]
    [TestCase("连接🚀")]
    public async Task AcceptsPortableNamesAndCaseInsensitivePlatforms(string name)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostConversionAsync(
            client, name, ValidYaml, ["android", "LINUX", "wInDoWs"]);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task RejectsUnknownAndDuplicatePlatforms()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage unknown = await PostConversionAsync(
            client,
            "unknown",
            ValidYaml,
            ["FreeBSD"]);
        using HttpResponseMessage duplicate = await PostConversionAsync(
            client,
            "duplicate",
            ValidYaml,
            ["Android", "android"]);

        await AssertProblemAsync(unknown, HttpStatusCode.BadRequest);
        await AssertProblemAsync(duplicate, HttpStatusCode.BadRequest);
    }

    [TestCase("proxies:\n  - name: [")]
    [TestCase("proxies:\n  - name: unsupported\n    type: unsupported")]
    public async Task ReturnsUnprocessableEntityForConversionFailures(
        string yaml)
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            "invalid",
            yaml,
            ["Android"]);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task RejectsOversizedRequestBody()
    {
        using HttpClient client = factory.CreateClient();
        string oversizedJson = "{\"padding\":\"" +
            new string('x', 4 * 1024 * 1024) +
            "\"}";
        using var content = new StringContent(
            oversizedJson,
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/convert",
            content);

        await AssertProblemAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task RejectsOversizedYaml()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            "oversized-yaml",
            new string('x', 2 * 1024 * 1024 + 1),
            ["Android"]);

        await AssertProblemAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task ConversionFailureDoesNotLeakInputOrPartialArtifacts()
    {
        using HttpClient client = factory.CreateClient();
        const string secret = "secret-node-credential-marker";
        string yaml = $"""
            proxies:
              - name: {secret}
                type: unsupported
                password: {secret}
            """;

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            "failure",
            yaml,
            ["Android", "Linux", "Windows"]);
        string problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                response.StatusCode,
                Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(problem, Does.Not.Contain(secret));
            Assert.That(problem, Does.Not.Contain("artifacts"));
            Assert.That(problem, Does.Not.Contain("stack"));
            Assert.That(problem, Does.Not.Contain("YamlDotNet"));
        });
    }

    [Test]
    public async Task UnexpectedEngineFailureReturnsInternalServerError()
    {
        using WebApplicationFactory<ServerProgram> failingFactory =
            factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IBoxForgeEngine>();
                    services.AddSingleton<IBoxForgeEngine>(new ThrowingEngine());
                }));
        using HttpClient client = failingFactory.CreateClient();

        using HttpResponseMessage response = await PostConversionAsync(
            client,
            "unexpected",
            ValidYaml,
            ["Android"]);
        string problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                response.StatusCode,
                Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(problem, Does.Not.Contain(nameof(InvalidOperationException)));
            Assert.That(problem, Does.Not.Contain("unexpected test failure"));
        });
    }

    [Test]
    public async Task RejectsSubscriptionUrlField()
    {
        using HttpClient client = factory.CreateClient();
        using var content = JsonContent.Create(new
        {
            name = "subscription",
            yaml = ValidYaml,
            platforms = new[] { "Android" },
            subscriptionUrl = "https://example.com/subscription"
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/convert",
            content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    private static Task<HttpResponseMessage> PostConversionAsync(
        HttpClient client,
        string name,
        string yaml,
        string[] platforms) =>
        client.PostAsJsonAsync(
            "/api/v1/convert",
            new { name, yaml, platforms });

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(
                response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json"));
            Assert.That(body, Does.Contain("\"status\""));
            Assert.That(body, Does.Not.Contain("stackTrace"));
        });
    }

    private sealed record ApiConvertResponse(
        string Name,
        IReadOnlyList<ApiArtifact> Artifacts);

    private sealed record ApiArtifact(
        string Platform,
        string Path,
        string Sha256,
        string Content);

    private sealed class ThrowingEngine : IBoxForgeEngine
    {
        public Task<ConversionBundle> ConvertAsync(
            ConversionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unexpected test failure");
    }
}
