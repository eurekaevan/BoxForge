using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoxForge.Engine;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using BoxForge.Models;
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
    public async Task ConcurrentProfilesRemainRequestLocalAndLegacyRequestsKeepDefaults()
    {
        using HttpClient client = factory.CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            bool enabled = index % 2 == 0;
            using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/convert", new
            {
                name = $"profile-{index}",
                yaml = ValidYaml,
                platforms = new[] { "Linux", "Android" },
                profile = new
                {
                    version = 1,
                    runtime = new
                    {
                        singbox_api = new { enabled },
                        tailscale = new { enabled, android_enabled = enabled }
                    },
                    platforms = new Dictionary<string, object>
                    {
                        ["Linux"] = new { multi_queue = enabled }
                    }
                }
            });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                await response.Content.ReadAsStringAsync());
            ApiConvertResponse result = (await response.Content.ReadFromJsonAsync<ApiConvertResponse>())!;
            Assert.That(result.Artifacts.Select(artifact => artifact.Platform),
                Is.EqualTo(new[] { "Android", "Linux" }));
            foreach (ApiArtifact artifact in result.Artifacts)
            {
                using JsonDocument config = JsonDocument.Parse(artifact.Content);
                Assert.That(config.RootElement.TryGetProperty("services", out _), Is.EqualTo(enabled));
                Assert.That(config.RootElement.TryGetProperty("endpoints", out _), Is.EqualTo(enabled));
                if (artifact.Platform == "Linux")
                    Assert.That(config.RootElement.GetProperty("inbounds")[0]
                        .GetProperty("multi_queue").GetBoolean(), Is.EqualTo(enabled));
            }
        }));

        using HttpResponseMessage baseline = await PostConversionAsync(client, "legacy", ValidYaml, ["Linux"]);
        Assert.That(baseline.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        ApiConvertResponse original = (await baseline.Content.ReadFromJsonAsync<ApiConvertResponse>())!;
        using JsonDocument defaults = JsonDocument.Parse(original.Artifacts.Single().Content);
        Assert.Multiple(() =>
        {
            Assert.That(defaults.RootElement.TryGetProperty("services", out _), Is.False);
            Assert.That(defaults.RootElement.TryGetProperty("endpoints", out _), Is.True);
            Assert.That(defaults.RootElement.GetProperty("inbounds")[0]
                .GetProperty("multi_queue").GetBoolean(), Is.True);
        });
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("true")]
    [TestCase("{}")]
    [TestCase("{\"version\":1,\"version\":1}")]
    [TestCase("{\"version\":2}")]
    [TestCase("{\"version\":1,\"runtime\":{\"singbox_api\":{\"enabled\":\"secret-profile-value\"}}}")]
    [TestCase("{\"version\":1,\"platforms\":{\"Linux\":{\"multi_queue\":\"secret-profile-value\"}}}")]
    [TestCase("{\"version\":1,\"unknown-secret-profile-value\":true}")]
    public async Task RejectsInvalidProfileWithoutEchoingValues(string profileJson)
    {
        using HttpClient client = factory.CreateClient();
        using JsonDocument profile = JsonDocument.Parse(profileJson);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/convert", new
        {
            name = "invalid-profile",
            yaml = ValidYaml,
            platforms = new[] { "Linux" },
            profile = profile.RootElement
        });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("secret-profile-value"));
    }

    [TestCase("{\"version\":1,\"secret-profile-value-\\uD800\":true}")]
    [TestCase("{\"version\":1,\"secret-profile-value-\\uDC00\":true}")]
    [TestCase("{\"version\":1,\"policies\":{\"url_test_url\":\"secret-profile-value-\\uD800\"}}")]
    [TestCase("{\"version\":1,\"policies\":{\"url_test_url\":\"secret-profile-value-\\uDC00\"}}")]
    public async Task RejectsUnpairedSurrogatesInProfileWithoutInternalErrors(string profileJson)
    {
        using HttpClient client = factory.CreateClient();
        string body = JsonSerializer.Serialize(new
        {
            name = "invalid-profile-unicode",
            yaml = ValidYaml,
            platforms = new[] { "Linux" }
        });
        using var content = new StringContent(
            body[..^1] + ",\"profile\":" + profileJson + "}",
            Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/convert", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        string problem = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(problem, Does.Not.Contain("secret-profile-value"));
            Assert.That(problem, Does.Not.Contain("InvalidOperationException"));
            Assert.That(problem, Does.Not.Contain("服务器内部错误"));
        });
    }

    [TestCase("\\uD800")]
    [TestCase("\\uDC00")]
    public async Task RejectsUnpairedSurrogatesInRequestPropertyNames(string escapedName)
    {
        using HttpClient client = factory.CreateClient();
        string body = JsonSerializer.Serialize(new
        {
            name = "invalid-request-unicode",
            yaml = ValidYaml,
            platforms = new[] { "Linux" }
        });
        using var content = new StringContent(
            body[..^1] + ",\"" + escapedName + "\":true}",
            Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/convert", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsDuplicateJsonProfileFields()
    {
        using HttpClient client = factory.CreateClient();
        string body = JsonSerializer.Serialize(new
        {
            name = "duplicate-profile",
            yaml = ValidYaml,
            platforms = new[] { "Linux" }
        });
        using var content = new StringContent(
            body[..^1] + ",\"profile\":{\"version\":1},\"Profile\":{\"version\":1}}",
            Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/convert", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [TestCase("application/json", HttpStatusCode.OK)]
    [TestCase("application/vnd.boxforge+json", HttpStatusCode.OK)]
    [TestCase("text/json", HttpStatusCode.BadRequest)]
    public async Task ValidatesJsonContentTypes(string contentType, HttpStatusCode expectedStatus)
    {
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(new { name = "json-type", yaml = ValidYaml, platforms = new[] { "Android" } }),
            Encoding.UTF8,
            contentType);

        using HttpResponseMessage response = await client.PostAsync("/api/v1/convert", content);

        if (expectedStatus == HttpStatusCode.OK)
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var result = (await response.Content.ReadFromJsonAsync<ApiConvertResponse>())!;
            Assert.That(result.Artifacts.Single().Platform, Is.EqualTo("Android"));
        }
        else
        {
            await AssertProblemAsync(response, expectedStatus);
            Assert.That(await response.Content.ReadAsStringAsync(),
                Does.Contain("application/json 或 application/*+json"));
        }
    }

    [Test]
    public async Task ReservedAddressResponseTagFailsTheWholeConversion()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostConversionAsync(
            client, "reserved-node", ValidYaml.Replace("name: test", "name: response-global-primary-a", StringComparison.Ordinal),
            ["Android", "Linux", "Windows"]);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("\"artifacts\""));
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

    [TestCase(false, null)]
    [TestCase(true, null)]
    [TestCase(true, "profile")]
    [TestCase(true, "tailscale")]
    [TestCase(true, "singbox_api")]
    public async Task KestrelValidatesDeploymentConfigurationBeforeServingRequests(
        bool tailscaleEnabled, string? invalidConfiguration)
    {
        string directory = Directory.CreateTempSubdirectory("boxforge-server-profile-").FullName;
        string profile = Path.Combine(directory, "profile.json");
        await File.WriteAllTextAsync(profile,
            """
            {"version":1,"regions":[{"id":"tw","display_name":"🇹🇼 TW","pattern":"TW[0-9]+"}],
             "services":[{"id":"ai","name":"AI Taiwan","default_region":"tw"},
                         {"id":"custom","name":"Custom Games","rule_sets":["games"],"precedes_domestic_routes":true}],
             "rule_sets":[{"tag":"ai","url":"https://rules.example.net/{tag}.srs","update_interval":"12h"}],
             "resolvers":[{"id":"alidns","server":"203.0.113.53","server_name":"dns.example.net"}]}
            """);
        if (invalidConfiguration == "profile")
        {
            await File.WriteAllTextAsync(profile,
                """{"version":1,"policies":{"dns_timeout":"invalid"}}""");
        }
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
        startInfo.Environment["BOXFORGE_Profile"] = profile;
        if (tailscaleEnabled)
        {
            // An explicit CLI path must override the environment path for Server too.
            startInfo.Environment["BOXFORGE_Profile"] = Path.Combine(directory, "missing.json");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(profile);
        }
        if (invalidConfiguration == "tailscale")
            startInfo.Environment["BOXFORGE_Tailscale__Enabled"] = "invalid";
        if (invalidConfiguration == "singbox_api")
            startInfo.Environment["BOXFORGE_SingboxApi__Enabled"] = "invalid";
        startInfo.Environment["Logging__LogLevel__Default"] = "Information";
        startInfo.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        using var process = Process.Start(startInfo)!;
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task<string>? outputTask = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            const string listeningPrefix = "Now listening on: ";
            if (invalidConfiguration != null)
            {
                outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                string output = await outputTask;
                string errors = await errorTask;
                string expectedDiagnostic = invalidConfiguration switch
                {
                    "profile" => "dns_timeout",
                    "tailscale" => "Tailscale:Enabled",
                    _ => "SingboxApi:Enabled"
                };
                Assert.Multiple(() =>
                {
                    Assert.That(process.ExitCode, Is.Not.EqualTo(0));
                    Assert.That(output, Does.Not.Contain(listeningPrefix));
                    Assert.That(output + errors, Does.Contain(expectedDiagnostic));
                });
                return;
            }

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
                client, "environment", ProfileConfigurationTests.Yaml("TW01", "TW02"), ["Android", "Linux", "Windows"]);
            Assert.That(converted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            ApiConvertResponse result = (await converted.Content.ReadFromJsonAsync<ApiConvertResponse>())!;
            Assert.That(result.Artifacts, Has.Count.EqualTo(3));
            foreach (ApiArtifact artifact in result.Artifacts)
            {
                using JsonDocument config = JsonDocument.Parse(artifact.Content);
                Assert.That(config.RootElement.GetProperty("outbounds").EnumerateArray()
                    .Select(outbound => outbound.GetProperty("tag").GetString()), Does.Contain("🇹🇼 TW AUTO"));
                Assert.That(config.RootElement.GetProperty("outbounds").EnumerateArray()
                    .Single(outbound => outbound.GetProperty("tag").GetString() == "AI Taiwan")
                    .GetProperty("default").GetString(), Is.EqualTo("🇹🇼 TW"));
                Assert.That(config.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
                    .Any(rule => rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == "Custom Games"), Is.True);
                var aiSource = config.RootElement.GetProperty("route").GetProperty("rule_set").EnumerateArray()
                    .Single(source => source.GetProperty("tag").ValueKind == JsonValueKind.String && source.GetProperty("tag").GetString() == "ai");
                Assert.That(aiSource.GetProperty("url").GetString(), Is.EqualTo("https://rules.example.net/ai.srs"));
                Assert.That(aiSource.GetProperty("update_interval").GetString(), Is.EqualTo("12h"));
                var bootstrap = config.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray()
                    .Single(server => server.GetProperty("tag").GetString() == "dns-direct-alidns");
                Assert.That(bootstrap.GetProperty("server").GetString(), Is.EqualTo("203.0.113.53"));
                Assert.That(bootstrap.GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("dns.example.net"));
                Assert.That(config.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
                    .Any(rule => rule.TryGetProperty("ip_cidr", out var cidrs) && cidrs[0].GetString() == "203.0.113.53/32"), Is.True);
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
            Directory.Delete(directory, recursive: true);
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
    [TestCase("proxies: null")]
    [TestCase("proxies: [null]")]
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task PlanDiagnosticsRemainPrivateInConversionAndExportResponses(bool export)
    {
        var logs = new RecordingLogProvider();
        using var failingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.RemoveAll<ISingboxConfigBuilder>();
            services.AddSingleton<ISingboxConfigBuilder>(provider =>
            {
                var profile = provider.GetRequiredService<BoxForgeProfile>();
                return new SingboxConfigBuilder(
                    new TailscaleEndpointBuilder(new BoxForgeProfile(BuiltInProfile.Default with
                    { Runtime = profile.Runtime with { Tailscale = new(false, true) } })),
                    provider.GetRequiredService<DnsProfileBuilder>(), provider.GetRequiredService<RouteProfileBuilder>(),
                    profile);
            });
        }));
        using HttpClient client = failingFactory.CreateClient();
        const string secret = "secret-plan-node-marker";
        string yaml = ValidYaml.Replace("name: test", "name: " + secret, StringComparison.Ordinal)
            .Replace("test-only", secret, StringComparison.Ordinal);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(yaml), "file", "plan-failure.yaml");
        foreach (string platform in new[] { "Android", "Linux", "Windows" })
            multipart.Add(new StringContent(platform), "platforms");
        using HttpResponseMessage response = export
            ? await client.PostAsync("/api/v1/export", multipart)
            : await PostConversionAsync(client, "plan-failure", yaml, ["Android", "Linux", "Windows"]);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        string problem = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(problem);
        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("detail").GetString(), Is.EqualTo("Clash YAML 无法转换为 sing-box 配置。"));
            Assert.That(response.Content.Headers.ContentDisposition, Is.Null);
            Assert.That(problem, Does.Not.Contain(secret).And.Not.Contain("IR004").And.Not.Contain("plan.dns")
                .And.Not.Contain("artifacts").And.Not.Contain("stack"));
            Assert.That(logs.Errors, Is.Empty);
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task UnexpectedEngineFailureReturnsInternalServerError(bool export, bool argumentError)
    {
        var logs = new RecordingLogProvider();
        using WebApplicationFactory<ServerProgram> failingFactory =
            factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.RemoveAll<ISingboxConfigBuilder>();
                    services.AddSingleton<ISingboxConfigBuilder>(new ThrowingConfigBuilder(argumentError));
                }));
        using HttpClient client = failingFactory.CreateClient();
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(ValidYaml), "file", "unexpected.yaml");
        multipart.Add(new StringContent("Android"), "platforms");
        using HttpResponseMessage response = export
            ? await client.PostAsync("/api/v1/export", multipart)
            : await PostConversionAsync(client, "unexpected", ValidYaml, ["Android"]);
        string problem = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(problem);
        string traceId = document.RootElement.GetProperty("traceId").GetString()!;
        string expectedType = argumentError ? nameof(ArgumentException) : nameof(InvalidOperationException);

        Assert.Multiple(() =>
        {
            Assert.That(
                response.StatusCode,
                Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(problem, Does.Not.Contain(expectedType));
            Assert.That(problem, Does.Not.Contain("unexpected test failure"));
            Assert.That(traceId, Is.Not.Empty);
            Assert.That(logs.Errors.Any(message => message.Contains(traceId, StringComparison.Ordinal)
                && message.Contains(expectedType, StringComparison.Ordinal)), Is.True,
                $"Response trace: {traceId}; captured errors: {string.Join(Environment.NewLine, logs.Errors)}");
            Assert.That(logs.Errors.All(message => !message.Contains("unexpected test failure", StringComparison.Ordinal)), Is.True);
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

    private sealed class ThrowingConfigBuilder(bool argumentError) : ISingboxConfigBuilder
    {
        public SingboxConfig Build(SingboxBuildRequest request) => throw (argumentError
            ? new ArgumentException("unexpected test failure")
            : new InvalidOperationException("unexpected test failure"));
    }

    private sealed class RecordingLogProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Errors.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }
}
