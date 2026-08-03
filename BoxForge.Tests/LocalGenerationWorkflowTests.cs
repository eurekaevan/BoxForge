using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Models;
using BoxForge.Parsers;
using BoxForge.Services;
using BoxForge.Workflows;

namespace BoxForge.Tests;

public sealed class LocalGenerationWorkflowTests
{
    private const string ValidShadowsocksYaml = """
        proxies:
          - name: test-node
            type: ss
            server: 127.0.0.1
            port: 8388
            cipher: aes-128-gcm
            password: secret
        """;

    private const string ValidHysteria2Yaml = """
        proxies:
          - name: test-hysteria2
            type: hysteria2
            server: example.com
            ports: 20000-30000
            password: secret
            sni: example.com
            client-fingerprint: chrome
            alpn: [h2, http/1.1]
            min-version: "1.3"
        """;

    private const string InvalidHysteriaYaml = """
        proxies:
          - name: invalid-node
            type: hysteria2
            server: example.com
            ports: nope-range
            password: secret
        """;

    private const string UnsupportedNodeYaml = """
        proxies:
          - name: unsupported-node
            type: vmess
            server: example.com
            port: 443
            uuid: 11111111-1111-1111-1111-111111111111
        """;

    [Fact]
    public async Task GenerateAsync_GeneratesYamlAndYmlForAllPlatforms()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            ValidShadowsocksYaml);
        await File.WriteAllTextAsync(
            Path.Combine(input, "beta.yml"),
            ValidShadowsocksYaml);

        var workflow = CreateWorkflow();
        var request = new LocalGenerationRequest(
            input,
            output,
            [
                TargetPlatform.Android,
                TargetPlatform.Linux,
                TargetPlatform.Windows
            ]);

        var firstRun = await workflow.GenerateAsync(request);

        Assert.Equal(new LocalGenerationSummary(6, 0, 0), firstRun);
        foreach (string configName in new[] { "alpha", "beta" })
        {
            foreach (TargetPlatform platform in request.Platforms)
            {
                Assert.True(File.Exists(Path.Combine(
                    output,
                    configName,
                    platform.ToString(),
                    "config.json")));
            }
        }

        var contentsBefore = Directory
            .EnumerateFiles(output, "config.json", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(output, path),
                File.ReadAllText,
                StringComparer.Ordinal);

        var secondRun = await workflow.GenerateAsync(request);

        Assert.Equal(new LocalGenerationSummary(0, 6, 0), secondRun);
        var contentsAfter = Directory
            .EnumerateFiles(output, "config.json", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(output, path),
                File.ReadAllText,
                StringComparer.Ordinal);
        Assert.Equal(contentsBefore, contentsAfter);
    }

    [Theory]
    [InlineData(TargetPlatform.Android, 1400)]
    [InlineData(TargetPlatform.Linux, null)]
    [InlineData(TargetPlatform.Windows, null)]
    public async Task GenerateAsync_SetsTunMtuByPlatform(
        TargetPlatform platform,
        int? expectedMtu)
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            ValidShadowsocksYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new LocalGenerationRequest(input, output, [platform]));

        Assert.Equal(new LocalGenerationSummary(1, 0, 0), summary);
        string content = await File.ReadAllTextAsync(Path.Combine(
            output,
            "alpha",
            platform.ToString(),
            "config.json"));
        using var document = JsonDocument.Parse(content);
        JsonElement tun = Assert.Single(
            document.RootElement.GetProperty("inbounds").EnumerateArray(),
            inbound => inbound.GetProperty("type").GetString() == "tun");

        if (expectedMtu is int mtu)
        {
            Assert.Equal(mtu, tun.GetProperty("mtu").GetInt32());
        }
        else
        {
            Assert.False(tun.TryGetProperty("mtu", out _));
        }
    }

    [Fact]
    public async Task GenerateAsync_PreservesExistingOutputWhenConversionFails()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.CreateDirectory("output");
        string marker = Path.Combine(output, "existing.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await File.WriteAllTextAsync(
            Path.Combine(input, "valid.yaml"),
            ValidShadowsocksYaml);
        await File.WriteAllTextAsync(
            Path.Combine(input, "broken.yaml"),
            InvalidHysteriaYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new LocalGenerationRequest(
                input,
                output,
                [TargetPlatform.Android]));

        Assert.Equal(1, summary.Failed);
        Assert.True(File.Exists(marker));
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        Assert.False(Directory.Exists(Path.Combine(output, "valid")));
    }

    [Fact]
    public async Task GenerateAsync_FailsForUnsupportedNodesInStrictMode()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "unsupported.yaml"),
            UnsupportedNodeYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new LocalGenerationRequest(
                input,
                output,
                [TargetPlatform.Windows]));

        Assert.Equal(new LocalGenerationSummary(0, 0, 1), summary);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerateAsync_ReplacesStaleOutputAfterCompleteSuccess()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.CreateDirectory("output");
        await File.WriteAllTextAsync(
            Path.Combine(output, "stale.txt"),
            "remove");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            ValidShadowsocksYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new LocalGenerationRequest(
                input,
                output,
                [TargetPlatform.Linux]));

        Assert.Equal(new LocalGenerationSummary(1, 0, 0), summary);
        Assert.False(File.Exists(Path.Combine(output, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "alpha",
            "Linux",
            "config.json")));
    }

    [Fact]
    public async Task GenerateAsync_UsesSingbox114ConfigurationFields()
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "alpha.yaml"),
            ValidShadowsocksYaml);

        var summary = await CreateWorkflow(new TailscaleOptions { Enabled = true })
            .GenerateAsync(new LocalGenerationRequest(
                input,
                output,
                [TargetPlatform.Android]));

        Assert.Equal(new LocalGenerationSummary(1, 0, 0), summary);
        string configPath = Path.Combine(
            output,
            "alpha",
            "Android",
            "config.json");
        string content = await File.ReadAllTextAsync(configPath);
        using var document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;

        Assert.Equal(
            "https://sing-box.sagernet.org/schema.json",
            root.GetProperty("$schema").GetString());
        JsonElement dns = root.GetProperty("dns");
        Assert.Equal(4096, dns.GetProperty("cache_capacity").GetInt32());
        Assert.True(dns.GetProperty("optimistic").GetProperty("enabled").GetBoolean());
        Assert.Equal(
            "12h",
            dns.GetProperty("optimistic").GetProperty("timeout").GetString());
        JsonElement tencent = Assert.Single(
            dns.GetProperty("servers").EnumerateArray(),
            server => server.GetProperty("tag").GetString() == "local-tencent");
        Assert.Equal("119.29.29.29", tencent.GetProperty("server").GetString());
        Assert.Equal(
            "doh.pub",
            tencent.GetProperty("tls").GetProperty("server_name").GetString());
        JsonElement google = Assert.Single(
            dns.GetProperty("servers").EnumerateArray(),
            server => server.GetProperty("tag").GetString() == "remote-google");
        Assert.Equal("8.8.8.8", google.GetProperty("server").GetString());
        Assert.Equal("🚀 PROXIES", google.GetProperty("detour").GetString());
        Assert.Equal(
            "dns.google",
            google.GetProperty("tls").GetProperty("server_name").GetString());
        JsonElement[] dnsRules = dns
            .GetProperty("rules")
            .EnumerateArray()
            .ToArray();
        JsonElement[] raceRules = dnsRules
            .Where(rule => rule.TryGetProperty("race", out _))
            .ToArray();
        Assert.Equal(4, raceRules.Length);
        Assert.Equal(
            4,
            dnsRules.Count(rule => rule.TryGetProperty("ip_accept_any", out _)));
        Assert.All(raceRules, rule =>
        {
            Assert.True(rule.GetProperty("race").GetBoolean());
            Assert.Equal("respond", rule.GetProperty("action").GetString());
            Assert.True(rule.GetProperty("ip_accept_any").GetBoolean());
            Assert.False(rule.TryGetProperty("response_rcode", out _));
            Assert.True(rule.TryGetProperty("match_response", out _));
        });
        Assert.Equal(
            2,
            dnsRules.Count(rule => rule.TryGetProperty("speculative", out _)));
        AssertDnsRaceRules(
            dnsRules,
            "cn",
            "local-tencent",
            "local");
        AssertDnsRaceRules(
            dnsRules,
            "global",
            "remote-google",
            "remote");
        JsonElement tunInbound = Assert.Single(
            root.GetProperty("inbounds").EnumerateArray(),
            inbound => inbound.GetProperty("type").GetString() == "tun");
        Assert.Equal("hijack", tunInbound.GetProperty("dns_mode").GetString());
        Assert.True(root
            .GetProperty("experimental")
            .GetProperty("cache_file")
            .GetProperty("store_dns")
            .GetBoolean());

        JsonElement httpClient = Assert.Single(
            root.GetProperty("http_clients").EnumerateArray());
        Assert.Equal("rule-set-download", httpClient.GetProperty("tag").GetString());
        Assert.Equal("DIRECT", httpClient.GetProperty("detour").GetString());
        Assert.Equal(
            "rule-set-download",
            root.GetProperty("route").GetProperty("default_http_client").GetString());

        JsonElement tailscaleDnsRule = Assert.Single(
            root.GetProperty("dns")
                .GetProperty("rules")
                .EnumerateArray(),
            rule => rule.TryGetProperty("preferred_by", out _));
        Assert.Equal(
            "tailscale-dns",
            Assert.Single(tailscaleDnsRule
                .GetProperty("preferred_by")
                .EnumerateArray())
                .GetString());
        Assert.Equal("route", tailscaleDnsRule.GetProperty("action").GetString());
        Assert.Equal("tailscale-dns", tailscaleDnsRule.GetProperty("server").GetString());
        Assert.True(tailscaleDnsRule
            .GetProperty("disable_optimistic_cache")
            .GetBoolean());
        Assert.Equal(
            "bootstrap",
            Assert.Single(root.GetProperty("endpoints").EnumerateArray())
                .GetProperty("domain_resolver")
                .GetString());

        Assert.DoesNotContain("\"download_detour\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"clash_api\"", content, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("services", out _));
    }

    private static void AssertDnsRaceRules(
        JsonElement[] rules,
        string responseTagPrefix,
        string firstServer,
        string secondServer)
    {
        string firstResponseTag = $"{responseTagPrefix}-first";
        string secondResponseTag = $"{responseTagPrefix}-second";
        int start = Array.FindIndex(
            rules,
            rule => rule.TryGetProperty("tag", out JsonElement tag)
                && tag.GetString() == firstResponseTag);
        Assert.True(start >= 0);

        JsonElement[] race = rules.Skip(start).Take(8).ToArray();
        Assert.Equal(8, race.Length);

        AssertRule(race[0], "evaluate", server: firstServer, tag: firstResponseTag);
        AssertRule(
            race[1],
            "respond",
            matchResponse: firstResponseTag,
            ipAcceptAny: true,
            isRace: true);
        AssertRule(
            race[2],
            "evaluate",
            server: secondServer,
            tag: secondResponseTag,
            speculative: true);
        AssertRule(
            race[3],
            "respond",
            matchResponse: secondResponseTag,
            ipAcceptAny: true,
            isRace: true);
        AssertRule(
            race[4],
            "respond",
            matchResponse: firstResponseTag,
            responseRcode: "NXDOMAIN");
        AssertRule(
            race[5],
            "respond",
            matchResponse: secondResponseTag,
            responseRcode: "NXDOMAIN");
        AssertRule(race[6], "respond", matchResponse: secondResponseTag);
        AssertRule(race[7], "route", server: secondServer);
    }

    private static void AssertRule(
        JsonElement rule,
        string action,
        string? server = null,
        string? tag = null,
        string? matchResponse = null,
        string? responseRcode = null,
        bool? ipAcceptAny = null,
        bool? isRace = null,
        bool? speculative = null)
    {
        Assert.Equal(action, rule.GetProperty("action").GetString());
        AssertOptionalString(rule, "server", server);
        AssertOptionalString(rule, "tag", tag);
        AssertOptionalString(rule, "match_response", matchResponse);
        AssertOptionalString(rule, "response_rcode", responseRcode);
        AssertOptionalBoolean(rule, "ip_accept_any", ipAcceptAny);
        AssertOptionalBoolean(rule, "race", isRace);
        AssertOptionalBoolean(rule, "speculative", speculative);
    }

    private static void AssertOptionalString(
        JsonElement element,
        string propertyName,
        string? expected)
    {
        Assert.Equal(
            expected is not null,
            element.TryGetProperty(propertyName, out JsonElement property));
        if (expected is not null)
        {
            Assert.Equal(expected, property.GetString());
        }
    }

    private static void AssertOptionalBoolean(
        JsonElement element,
        string propertyName,
        bool? expected)
    {
        Assert.Equal(
            expected.HasValue,
            element.TryGetProperty(propertyName, out JsonElement property));
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, property.GetBoolean());
        }
    }

    [Theory]
    [InlineData(TargetPlatform.Linux, "/etc/sing-box/ui")]
    [InlineData(TargetPlatform.Windows, "ui")]
    public async Task GenerateAsync_UsesSingboxApiAndHysteria2HopIntervals(
        TargetPlatform platform,
        string dashboardPath)
    {
        using var temporary = new TemporaryDirectory();
        string input = temporary.CreateDirectory("input");
        string output = temporary.GetPath("output");
        await File.WriteAllTextAsync(
            Path.Combine(input, "hysteria2.yaml"),
            ValidHysteria2Yaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new LocalGenerationRequest(input, output, [platform]));

        Assert.Equal(new LocalGenerationSummary(1, 0, 0), summary);
        string content = await File.ReadAllTextAsync(Path.Combine(
            output,
            "hysteria2",
            platform.ToString(),
            "config.json"));
        using var document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;

        JsonElement hysteria2 = Assert.Single(
            root.GetProperty("outbounds").EnumerateArray(),
            outbound => outbound.GetProperty("type").GetString() == "hysteria2");
        Assert.Equal("30s", hysteria2.GetProperty("hop_interval").GetString());
        Assert.Equal("60s", hysteria2.GetProperty("hop_interval_max").GetString());
        Assert.Equal("standard", hysteria2.GetProperty("bbr_profile").GetString());
        JsonElement tls = hysteria2.GetProperty("tls");
        Assert.False(tls.TryGetProperty("alpn", out _));
        Assert.False(tls.TryGetProperty("min_version", out _));
        Assert.False(tls.TryGetProperty("utls", out _));
        JsonElement nodeDnsRule = Assert.Single(
            root.GetProperty("dns").GetProperty("rules").EnumerateArray(),
            rule => rule.TryGetProperty("domain", out _));
        Assert.True(nodeDnsRule
            .GetProperty("disable_optimistic_cache")
            .GetBoolean());

        JsonElement api = Assert.Single(root.GetProperty("services").EnumerateArray());
        Assert.Equal("api", api.GetProperty("type").GetString());
        Assert.Equal("api", api.GetProperty("tag").GetString());
        Assert.Equal("127.0.0.1", api.GetProperty("listen").GetString());
        Assert.Equal(9090, api.GetProperty("listen_port").GetInt32());
        Assert.Equal("127001", api.GetProperty("secret").GetString());
        JsonElement dashboard = api.GetProperty("dashboard");
        Assert.True(dashboard.GetProperty("enabled").GetBoolean());
        Assert.Equal(dashboardPath, dashboard.GetProperty("path").GetString());
        Assert.Equal(
            "rule-set-download",
            dashboard.GetProperty("http_client").GetString());

        Assert.DoesNotContain("\"clash_api\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RejectsOutputDirectoryContainingInput()
    {
        using var temporary = new TemporaryDirectory();
        string output = temporary.CreateDirectory("output");
        string input = Path.Combine(output, "input");
        Directory.CreateDirectory(input);
        string inputFile = Path.Combine(input, "alpha.yaml");
        await File.WriteAllTextAsync(inputFile, ValidShadowsocksYaml);

        var summary = await CreateWorkflow().GenerateAsync(
            new LocalGenerationRequest(
                input,
                output,
                [TargetPlatform.Android]));

        Assert.Equal(new LocalGenerationSummary(0, 0, 1), summary);
        Assert.True(File.Exists(inputFile));
    }

    private static LocalGenerationWorkflow CreateWorkflow(
        TailscaleOptions? tailscaleOptionsValue = null)
    {
        var singboxOptions = Options.Create(new SingboxOptions());
        var tailscaleOptions = Options.Create(
            tailscaleOptionsValue ?? new TailscaleOptions());
        IProxyConverter[] converters =
        [
            new TrojanConverter(),
            new VlessConverter(),
            new Hysteria2Converter(),
            new ShadowsocksConverter(),
            new AnyTlsConverter()
        ];
        var nodeCatalogBuilder = new NodeCatalogBuilder(
            converters,
            NullLogger<NodeCatalogBuilder>.Instance);
        var configBuilder = new SingboxConfigBuilder(
            nodeCatalogBuilder,
            new ProfilePlanner(singboxOptions),
            new InboundBuilder(),
            new TailscaleEndpointBuilder(tailscaleOptions),
            new DnsProfileBuilder(singboxOptions, tailscaleOptions),
            new RouteProfileBuilder(singboxOptions, tailscaleOptions),
            new ServiceBuilder(),
            new ExperimentalBuilder());
        var serializer = new ConfigSerializer();
        var conversionService = new ConversionService(
            new ClashParser(),
            configBuilder,
            new SingboxConfigValidator(),
            serializer);

        return new LocalGenerationWorkflow(
            conversionService,
            NullLogger<LocalGenerationWorkflow>.Instance);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            $"BoxForge.Tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string GetPath(string relativePath) =>
            Path.Combine(root, relativePath);

        public string CreateDirectory(string relativePath)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
