using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoxForge.Engine;
using BoxForge.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ServerProgram = BoxForge.Server.Program;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ServerExportApiTests
{
    private const int MaxYamlBytes = 2 * 1024 * 1024;

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
    public async Task SinglePlatformUploadReturnsZipDownload()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "single.yaml",
            name: null,
            ["Android"]);
        IReadOnlyList<ArchiveContent> entries = await ReadArchiveAsync(response);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/zip"));
            Assert.That(
                response.Content.Headers.ContentDisposition?.ToString(),
                Is.EqualTo(
                    "attachment; filename=\"boxforge-output.zip\""));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(
                entries.Select(entry => entry.Path),
                Is.EqualTo(new[] { "single/Android/config.json" }));
        });
    }

    [Test]
    public async Task ConcurrentExportProfilesRemainRequestLocalAndLegacyRequestsKeepDefaults()
    {
        using HttpClient client = factory.CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            bool enabled = index % 2 == 0;
            using MultipartFormDataContent form = CreateExportForm(
                Encoding.UTF8.GetBytes(ValidYaml), "profile.yaml", null, ["Linux", "Android"]);
            form.Add(new StringContent(JsonSerializer.Serialize(new
            {
                features = new
                {
                    api = new { enabled },
                    tailscale = new { enabled }
                },
                platforms = new { Linux = new { route = new { multi_queue = enabled } } }
            })), "profile");
            using HttpResponseMessage response = await client.PostAsync("/api/v1/export", form);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                await response.Content.ReadAsStringAsync());
            IReadOnlyList<ArchiveContent> entries = await ReadArchiveAsync(response);
            Assert.That(entries.Select(entry => entry.Path), Is.EqualTo(new[]
            {
                "profile/Android/config.json", "profile/Linux/config.json"
            }));
            foreach (ArchiveContent entry in entries)
            {
                using JsonDocument config = JsonDocument.Parse(entry.Bytes);
                Assert.That(config.RootElement.TryGetProperty("services", out _), Is.EqualTo(enabled));
                Assert.That(config.RootElement.TryGetProperty("endpoints", out _), Is.EqualTo(enabled));
                if (entry.Path.Contains("/Linux/", StringComparison.Ordinal))
                    Assert.That(config.RootElement.GetProperty("inbounds")[0]
                        .GetProperty("multi_queue").GetBoolean(), Is.EqualTo(enabled));
            }
        }));

        using HttpResponseMessage baseline = await PostExportAsync(
            client, Encoding.UTF8.GetBytes(ValidYaml), "legacy.yaml", null, ["Linux"]);
        Assert.That(baseline.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        IReadOnlyList<ArchiveContent> original = await ReadArchiveAsync(baseline);
        using JsonDocument defaults = JsonDocument.Parse(original.Single().Bytes);
        Assert.Multiple(() =>
        {
            Assert.That(defaults.RootElement.TryGetProperty("services", out _), Is.False);
            Assert.That(defaults.RootElement.TryGetProperty("endpoints", out _), Is.True);
            Assert.That(defaults.RootElement.GetProperty("inbounds")[0]
                .GetProperty("multi_queue").GetBoolean(), Is.True);
        });
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("true")]
    [TestCase("{\"features\":{},\"features\":{}}")]
    [TestCase("{\"version\":1}")]
    [TestCase("{\"features\":{\"api\":{\"enabled\":\"secret-profile-value\"}}}")]
    [TestCase("{\"platforms\":{\"Linux\":{\"route\":{\"multi_queue\":\"secret-profile-value\"}}}}")]
    [TestCase("{\"unknown-secret-profile-value\":true}")]
    [TestCase("{\"platforms\":{\"Windows\":{\"features\":{\"api\":{\"enabled\":\"secret-profile-value\"}}}}}")]
    [TestCase("{\"platforms\":{\"Windows\":{\"route\":{\"sniff_timeout\":[]}}}}")]
    [TestCase("{\"platforms\":{\"Windows\":{\"features\":null}}}")]
    public async Task RejectsInvalidExportProfileWithoutEchoingValues(string profileJson)
    {
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = CreateExportForm(
            Encoding.UTF8.GetBytes(ValidYaml), "profile.yaml", null, ["Linux"]);
        form.Add(new StringContent(profileJson), "profile");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/export", form);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("secret-profile-value"));
    }

    [TestCase("{\"secret-profile-value-\\uD800\":true}")]
    [TestCase("{\"secret-profile-value-\\uDC00\":true}")]
    [TestCase("{\"tuning\":{\"url_test_url\":\"secret-profile-value-\\uD800\"}}")]
    [TestCase("{\"tuning\":{\"url_test_url\":\"secret-profile-value-\\uDC00\"}}")]
    public async Task RejectsUnpairedSurrogatesInExportProfileWithoutInternalErrors(string profileJson)
    {
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = CreateExportForm(
            Encoding.UTF8.GetBytes(ValidYaml), "profile.yaml", null, ["Linux"]);
        form.Add(new StringContent(profileJson), "profile");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/export", form);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        string problem = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(problem, Does.Not.Contain("secret-profile-value"));
            Assert.That(problem, Does.Not.Contain("InvalidOperationException"));
            Assert.That(problem, Does.Not.Contain("服务器内部错误"));
        });
    }

    [Test]
    public async Task RejectsDuplicateExportProfileFields()
    {
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = CreateExportForm(
            Encoding.UTF8.GetBytes(ValidYaml), "profile.yaml", null, ["Linux"]);
        form.Add(new StringContent("{}"), "profile");
        form.Add(new StringContent("{}"), "profile");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/export", form);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ThreePlatformArchiveHasFixedPathsAndOrder()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "source.yml",
            "exported",
            ["Windows", "Android", "Linux"]);
        IReadOnlyList<ArchiveContent> entries = await ReadArchiveAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            entries.Select(entry => entry.Path),
            Is.EqualTo(new[]
            {
                "exported/Android/config.json",
                "exported/Linux/config.json",
                "exported/Windows/config.json"
            }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ArchiveContentMatchesEngineUtf8Bytes(bool includeBom)
    {
        using HttpClient client = factory.CreateClient();
        IBoxForgeEngine engine =
            factory.Services.GetRequiredService<IBoxForgeEngine>();
        var request = new ConversionRequest(
            "matching-export",
            ValidYaml,
            [TargetPlatform.Windows, TargetPlatform.Android]);
        ConversionBundle expected = await engine.ConvertAsync(request);
        byte[] yamlBytes = Encoding.UTF8.GetBytes(request.ClashYaml);
        if (includeBom)
        {
            yamlBytes = [0xEF, 0xBB, 0xBF, .. yamlBytes];
        }

        using HttpResponseMessage response = await PostExportAsync(
            client,
            yamlBytes,
            "ignored.yaml",
            request.Name,
            ["Windows", "Android"]);
        IReadOnlyList<ArchiveContent> actual = await ReadArchiveAsync(response);

        Assert.That(actual, Has.Count.EqualTo(expected.Artifacts.Count));
        for (var index = 0; index < expected.Artifacts.Count; index++)
        {
            ConversionArtifact artifact = expected.Artifacts[index];
            Assert.Multiple(() =>
            {
                Assert.That(
                    actual[index].Path,
                    Is.EqualTo(
                        $"{request.Name}/{artifact.Platform}/config.json"));
                Assert.That(
                    actual[index].Bytes,
                    Is.EqualTo(Encoding.UTF8.GetBytes(artifact.Content)));
            });
        }
    }

    [TestCase(" \r\n\t")]
    [TestCase("\uFEFF")]
    [TestCase("\uFEFF \n")]
    public async Task RejectsBlankDecodedYaml(string yaml)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostExportAsync(
            client, Encoding.UTF8.GetBytes(yaml), "empty.yaml", null, ["Android"]);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task RejectsEmptyFile()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            [],
            "empty.yaml",
            name: null,
            ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsMissingFile()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            fileBytes: null,
            "missing.yaml",
            name: null,
            ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsMultipleFiles()
    {
        using HttpClient client = factory.CreateClient();
        using var form = CreateExportForm(
            Encoding.UTF8.GetBytes(ValidYaml),
            "one.yaml",
            name: null,
            ["Android"]);
        AddFile(form, Encoding.UTF8.GetBytes(ValidYaml), "two.yaml");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/export",
            form);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsNonYamlFileExtension()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "config.txt",
            name: null,
            ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsUnsupportedFormFields()
    {
        using HttpClient client = factory.CreateClient();
        using var form = CreateExportForm(
            Encoding.UTF8.GetBytes(ValidYaml),
            "config.yaml",
            name: null,
            ["Android"]);
        form.Add(
            new StringContent("https://example.com/subscription"),
            "subscriptionUrl");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/export",
            form);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsMultipartPrefixThatIsNotMultipartMediaType()
    {
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent("not a multipart request");
        content.Headers.ContentType = new MediaTypeHeaderValue(
            "multipart/form-data-invalid");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/export",
            content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsMultipartWithoutFinalBoundary()
    {
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent(
            "--test-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"input.yaml\"\r\n\r\nprivate-upload-content");
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        content.Headers.ContentType.Parameters.Add(
            new NameValueHeaderValue("boundary", "test-boundary"));

        using HttpResponseMessage response = await client.PostAsync("/api/v1/export", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        string problem = await response.Content.ReadAsStringAsync();
        Assert.That(problem, Does.Not.Contain("private-upload-content"));
        Assert.That(problem, Does.Not.Contain("IOException"));
    }

    [Test]
    public async Task RejectsFileOverTwoMiB()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            new byte[MaxYamlBytes + 1],
            "oversized.yaml",
            name: null,
            ["Android"]);

        await AssertProblemAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task RejectsInvalidAndDuplicatePlatforms()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage empty = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "empty-platform.yaml",
            name: null,
            []);
        using HttpResponseMessage invalid = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "invalid-platform.yaml",
            name: null,
            ["FreeBSD"]);
        using HttpResponseMessage duplicate = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "duplicate-platform.yaml",
            name: null,
            ["Android", "android"]);

        await AssertProblemAsync(empty, HttpStatusCode.BadRequest);
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest);
        await AssertProblemAsync(duplicate, HttpStatusCode.BadRequest);
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

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "safe.yaml",
            name,
            ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsConfigurationNameLongerThanOneHundredRunes()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "safe.yaml",
            new string('a', 101),
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
        using HttpResponseMessage response = await PostExportAsync(
            client, Encoding.UTF8.GetBytes(ValidYaml), "safe.yaml", null, [platform]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [TestCase(85, HttpStatusCode.OK)]
    [TestCase(86, HttpStatusCode.BadRequest)]
    public async Task ValidatesFilenameStemUtf8ByteLimit(
        int runeCount,
        HttpStatusCode expectedStatus)
    {
        using HttpClient client = factory.CreateClient();
        string name = new('中', runeCount);
        using HttpResponseMessage response = await PostExportAsync(
            client, Encoding.UTF8.GetBytes(ValidYaml), $"{name}.yaml", null, ["android"]);

        Assert.That(response.StatusCode, Is.EqualTo(expectedStatus));
        if (expectedStatus == HttpStatusCode.OK)
        {
            IReadOnlyList<ArchiveContent> entries = await ReadArchiveAsync(response);
            Assert.That(entries.Single().Path, Is.EqualTo($"{name}/Android/config.json"));
        }
    }

    [Test]
    public async Task RejectsMalformedUtf8AfterBom()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostExportAsync(
            client, [0xEF, 0xBB, 0xBF, 0xFF], "safe.yaml", null, ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
    }

    [TestCase("CON.yaml")]
    [TestCase("bad:name.yml")]
    public async Task RejectsNonportableFilenameStemWhenNameIsOmitted(string fileName)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostExportAsync(
            client, Encoding.UTF8.GetBytes(ValidYaml), fileName, null, ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RejectsZipSlipFileNameWhenNameIsOmitted()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(ValidYaml),
            "../../escape.yaml",
            name: null,
            ["Android"]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CorruptYamlReturnsUnprocessableEntity()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes("proxies:\n  - name: ["),
            "corrupt.yaml",
            name: null,
            ["Android"]);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task ConversionFailureDoesNotReturnPartialArchive()
    {
        using HttpClient client = factory.CreateClient();
        const string secret = "uploaded-secret-credential";
        string yaml = $"""
            proxies:
              - name: {secret}
                type: unsupported
                password: {secret}
            """;

        using HttpResponseMessage response = await PostExportAsync(
            client,
            Encoding.UTF8.GetBytes(yaml),
            "failure.yaml",
            name: null,
            ["Android", "Linux", "Windows"]);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                response.StatusCode,
                Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(
                response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json"));
            Assert.That(body, Does.Not.Contain(secret));
            Assert.That(body, Does.Not.Contain("config.json"));
            Assert.That(body, Does.Not.Contain("artifacts"));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    [Test]
    public async Task RootPageUsesLocalResourcesAndRestrictsConnectionsToSameOrigin()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage pageResponse = await client.GetAsync("/");
        string page = await pageResponse.Content.ReadAsStringAsync();
        using HttpResponseMessage stylesResponse = await client.GetAsync(
            "/styles.css");
        using HttpResponseMessage scriptResponse = await client.GetAsync(
            "/app.js");
        string styles = await stylesResponse.Content.ReadAsStringAsync();

        // Inspect resource declarations, allowing ordinary links and explanatory text.
        string resourceMarkup = Regex.Replace(
            page,
            @"<!--.*?-->",
            "",
            RegexOptions.Singleline);
        string styleDeclarations = Regex.Replace(
            styles,
            @"/\*.*?\*/",
            "",
            RegexOptions.Singleline);
        const string externalHtmlResource = """
            (?is)<(?:script|link|img|iframe|source|video|audio|object|embed)\b[^>]*\s(?:src|href|poster|data)\s*=\s*["']?(?:https?:|//)
            """;
        const string externalCssResource = """
            (?i)(?:url\(\s*["']?|@import\s+["'])(?:https?:|//)
            """;
        string[] contentSecurityPolicy = pageResponse.Headers
            .GetValues("Content-Security-Policy").Single()
            .Split(
                ';',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.Multiple(() =>
        {
            Assert.That(pageResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                pageResponse.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("text/html"));
            Assert.That(stylesResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(scriptResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(page, Does.Contain("id=\"export-form\""));
            Assert.That(page, Does.Contain("不会被持久保存"));
            Assert.That(resourceMarkup, Does.Not.Match(externalHtmlResource));
            Assert.That(page, Does.Not.Contain("type=\"url\""));
            Assert.That(styleDeclarations, Does.Not.Match(externalCssResource));
            // CSP also restricts dynamic resources and JavaScript network requests.
            Assert.That(contentSecurityPolicy, Is.EquivalentTo(new[]
            {
                "default-src 'none'",
                "script-src 'self'",
                "style-src 'self'",
                "connect-src 'self'",
                "img-src 'self' data:",
                "base-uri 'none'",
                "form-action 'self'",
                "frame-ancestors 'none'",
                "object-src 'none'"
            }));
            Assert.That(
                pageResponse.Headers.GetValues("X-Content-Type-Options")
                    .Single(),
                Is.EqualTo("nosniff"));
            Assert.That(
                pageResponse.Headers.GetValues("Referrer-Policy").Single(),
                Is.EqualTo("no-referrer"));
            Assert.That(pageResponse.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    private static async Task<HttpResponseMessage> PostExportAsync(
        HttpClient client,
        byte[]? fileBytes,
        string fileName,
        string? name,
        string[] platforms)
    {
        using MultipartFormDataContent form = CreateExportForm(
            fileBytes,
            fileName,
            name,
            platforms);
        return await client.PostAsync("/api/v1/export", form);
    }

    private static MultipartFormDataContent CreateExportForm(
        byte[]? fileBytes,
        string fileName,
        string? name,
        string[] platforms)
    {
        var form = new MultipartFormDataContent();
        if (fileBytes != null)
        {
            AddFile(form, fileBytes, fileName);
        }

        if (name != null)
        {
            form.Add(new StringContent(name), "name");
        }

        foreach (string platform in platforms)
        {
            form.Add(new StringContent(platform), "platforms");
        }

        return form;
    }

    private static void AddFile(
        MultipartFormDataContent form,
        byte[] bytes,
        string fileName)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/octet-stream");
        form.Add(file, "file", fileName);
    }

    private static async Task<IReadOnlyList<ArchiveContent>> ReadArchiveAsync(
        HttpResponseMessage response)
    {
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        using var buffer = new MemoryStream(bytes);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entries = new List<ArchiveContent>(archive.Entries.Count);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            await using Stream source = entry.Open();
            using var content = new MemoryStream();
            await source.CopyToAsync(content);
            entries.Add(new ArchiveContent(entry.FullName, content.ToArray()));
        }

        return entries;
    }

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
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    private sealed record ArchiveContent(string Path, byte[] Bytes);
}
