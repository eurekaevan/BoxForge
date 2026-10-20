using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProfileConfigurationTests
{
    private const string TaiwanProfile = """
        {"version":1,"regions":[{"id":"tw","display_name":"🇹🇼 TW","pattern":"台湾|taiwan|TW[0-9]+"}]}
        """;

    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory("boxforge-profile-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public void OverridesInheritFieldsReplacePatternsAndAppendNewRegions()
    {
        var profile = Load("""
            {"version":1,"regions":[
              {"id":"us","display_name":"US custom"},
              {"id":"jp","pattern":"custom-japan"},
              {"id":"tw","display_name":"🇹🇼 TW","pattern":"taiwan"}
            ]}
            """);
        Assert.That(profile.Regions.Select(region => region.Id), Is.EqualTo(new[] { "us", "jp", "hk", "sg", "tw" }));
        Assert.That(profile.Regions[0].Pattern.ToString(), Is.EqualTo(BoxForgeProfile.Default.Regions[0].Pattern.ToString()));
        Assert.That(profile.Regions[1].DisplayName, Is.EqualTo("🇯🇵 JP"));
        Assert.That(profile.Regions[1].Pattern.IsMatch("日本"), Is.False);
        Assert.That(profile.Regions[1].Pattern.IsMatch("CUSTOM-JAPAN"), Is.True);
        var plan = Plan(new NodeCatalog([], ["US01", "US02"]), profile);
        Assert.That(plan.MainOutbound.Default, Is.EqualTo("US custom"));
        Assert.That(plan.ServiceOutbounds.Single(service => service.Tag == ServiceGroupNames.Ai).Default, Is.EqualTo("US custom"));
        Assert.That(BoxForgeProfile.Default.Regions[0].DisplayName, Is.EqualTo("🇺🇸 US"));
    }

    [TestCase("{\"version\":1}")]
    [TestCase("{\"version\":1,\"regions\":[]}")]
    [TestCase("{\"version\":1,\"services\":[]}")]
    [TestCase("{\"version\":1,\"rule_sets\":[]}")]
    [TestCase("{\"version\":1,\"resolvers\":[]}")]
    [TestCase("{\"version\":1,\"platforms\":{}}")]
    [TestCase("{\"version\":1,\"runtime\":{}}")]
    [TestCase("{\"version\":1,\"policies\":{}}")]
    public void EmptyOverridePreservesDefaults(string json)
    {
        Assert.That(Load(json).Regions.Select(region => (region.Id, region.DisplayName, region.Pattern.ToString())),
            Is.EqualTo(BoxForgeProfile.Default.Regions.Select(region => (region.Id, region.DisplayName, region.Pattern.ToString()))));
        Assert.That(Load(json).Services, Is.EqualTo(BoxForgeProfile.Default.Services));
        Assert.That(Load(json).ToResolvedJson(), Is.EqualTo(BoxForgeProfile.Default.ToResolvedJson()));
    }

    [Test]
    public void PlatformOverridesInheritFieldsClearNullableValuesAndReplaceArrays()
    {
        var profile = Load("""
            {"version":1,"platforms":{"Linux":{"strict_route":null,"auto_redirect":null,
              "multi_queue":null,"tcp_keep_alive":null,"tcp_keep_alive_interval":null,
              "connect_timeout":"8s","tun_addresses":["10.30.0.1/30","fd30::1/126"]}},
             "policies":{"sniff_excluded_ports":[22]}}
            """);
        var linux = profile.GetPlatform(TargetPlatform.Linux);
        var original = BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux);
        var resolved = JsonNode.Parse(profile.ToResolvedJson())!;
        var defaults = JsonNode.Parse(BoxForgeProfile.Default.ToResolvedJson())!;
        Assert.Multiple(() =>
        {
            Assert.That(linux.StrictRoute, Is.Null);
            Assert.That(linux.AutoRedirect, Is.Null);
            Assert.That(linux.MultiQueue, Is.Null);
            Assert.That(linux.TcpKeepAlive, Is.Null);
            Assert.That(linux.TcpKeepAliveInterval, Is.Null);
            Assert.That(linux.ConnectTimeout, Is.EqualTo("8s"));
            Assert.That(linux.UrlTestInterval, Is.EqualTo(original.UrlTestInterval));
            Assert.That(linux.DnsSelectionMode, Is.EqualTo(original.DnsSelectionMode));
            Assert.That(linux.TunAddresses, Is.EqualTo(new[] { "10.30.0.1/30", "fd30::1/126" }));
            Assert.That(profile.Policies.SniffExcludedPorts, Is.EqualTo(new[] { 22 }));
            Assert.That(profile.Policies.SniffTimeout, Is.EqualTo(BoxForgeProfile.Default.Policies.SniffTimeout));
            Assert.That(JsonNode.DeepEquals(resolved["platforms"]!["Android"], defaults["platforms"]!["Android"]), Is.True);
            Assert.That(JsonNode.DeepEquals(resolved["platforms"]!["Windows"], defaults["platforms"]!["Windows"]), Is.True);
            Assert.That(original.StrictRoute, Is.True);
            Assert.That(original.TunAddresses, Is.EqualTo(new[] { "172.19.0.1/30", "fd00::1/126" }));
        });
    }

    [TestCase(null, null, false)]
    [TestCase(null, "true", true)]
    [TestCase("false", "true", false)]
    [TestCase("true", "false", true)]
    public async Task RuntimeFileValuesAreOverriddenOnlyByPresentHostKeys(string? nested, string? legacy, bool expected)
    {
        string path = Path.Combine(directory, "runtime.json");
        await File.WriteAllTextAsync(path, """
            {"version":1,"runtime":{"tailscale":{"enabled":false,"android_enabled":false},
             "singbox_api":{"enabled":true}}}
            """);
        var values = new Dictionary<string, string?>();
        if (nested is not null) values["Tailscale:Enabled"] = nested;
        if (legacy is not null) values["TailscaleEnabled"] = legacy;
        using var provider = CreateProvider(path, values);
        var profile = provider.GetRequiredService<BoxForgeProfile>();
        Assert.Multiple(() =>
        {
            Assert.That(profile.Runtime.Tailscale.Enabled, Is.EqualTo(expected));
            Assert.That(profile.Runtime.Tailscale.AndroidEnabled, Is.False);
            Assert.That(profile.Runtime.SingboxApi.Enabled, Is.True);
        });
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("runtime", Yaml("US01"), Enum.GetValues<TargetPlatform>()));
        foreach (var artifact in bundle.Artifacts)
        {
            using var json = JsonDocument.Parse(artifact.Content);
            Assert.That(json.RootElement.TryGetProperty("endpoints", out _),
                Is.EqualTo(artifact.Platform != TargetPlatform.Android && expected));
            Assert.That(json.RootElement.GetProperty("services")[0].GetProperty("type").GetString(), Is.EqualTo("api"));
        }
    }

    [Test]
    public async Task AndroidAndApiHostKeysOverrideFileRuntimeIndependently()
    {
        string path = Path.Combine(directory, "runtime.json");
        await File.WriteAllTextAsync(path, """
            {"version":1,"runtime":{"tailscale":{"enabled":false,"android_enabled":false},
             "singbox_api":{"enabled":true}}}
            """);
        using var provider = CreateProvider(path, new Dictionary<string, string?>
        {
            ["Tailscale:AndroidEnabled"] = "true",
            ["TailscaleAndroidEnabled"] = "false",
            ["SingboxApi:Enabled"] = "false"
        });
        var profile = provider.GetRequiredService<BoxForgeProfile>();
        Assert.Multiple(() =>
        {
            Assert.That(profile.Runtime.Tailscale.Enabled, Is.False);
            Assert.That(profile.Runtime.Tailscale.AndroidEnabled, Is.True);
            Assert.That(profile.Runtime.SingboxApi.Enabled, Is.False);
        });
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("runtime", Yaml("US01"), Enum.GetValues<TargetPlatform>()));
        foreach (var artifact in bundle.Artifacts)
        {
            using var json = JsonDocument.Parse(artifact.Content);
            Assert.That(json.RootElement.TryGetProperty("endpoints", out _), Is.EqualTo(artifact.Platform == TargetPlatform.Android));
            Assert.That(json.RootElement.TryGetProperty("services", out _), Is.False);
        }
    }

    [Test]
    public void RequestOverlayWinsOverHostWithoutMutatingAnySnapshotOrInput()
    {
        var host = BoxForgeProfile.Default.ApplyOverrides(JsonNode.Parse("""
            {"version":1,"runtime":{"tailscale":{"enabled":false}},
             "platforms":{"Linux":{"connect_timeout":"8s"}},"policies":{"sniff_excluded_ports":[22]}}
            """)!.AsObject());
        var request = JsonNode.Parse("""
            {"version":1,"runtime":{"tailscale":{"enabled":true}},
             "platforms":{"Linux":{"connect_timeout":"9s"}},"policies":{"sniff_excluded_ports":[443]}}
            """)!.AsObject();
        var first = host.ApplyOverrides(request);
        request["policies"]!["sniff_excluded_ports"]![0] = 8443;
        var second = host.ApplyOverrides(request);
        Assert.Multiple(() =>
        {
            Assert.That(first.Runtime.Tailscale.Enabled, Is.True);
            Assert.That(first.Runtime.Tailscale.AndroidEnabled, Is.EqualTo(host.Runtime.Tailscale.AndroidEnabled));
            Assert.That(first.GetPlatform(TargetPlatform.Linux).ConnectTimeout, Is.EqualTo("9s"));
            Assert.That(first.Policies.SniffExcludedPorts, Is.EqualTo(new[] { 443 }));
            Assert.That(second.Policies.SniffExcludedPorts, Is.EqualTo(new[] { 8443 }));
            Assert.That(host.Policies.SniffExcludedPorts, Is.EqualTo(new[] { 22 }));
            Assert.That(host.Runtime.Tailscale.Enabled, Is.False);
            Assert.That(host.GetPlatform(TargetPlatform.Linux).ConnectTimeout, Is.EqualTo("8s"));
            Assert.That(BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux).ConnectTimeout, Is.EqualTo("5s"));
            Assert.That(BoxForgeProfile.Default.Policies.SniffExcludedPorts, Is.EqualTo(new[] { 21, 22, 3306, 5900 }));
        });
    }

    [TestCase("\"platforms\":{\"MacOS\":{}}")]
    [TestCase("\"platforms\":{\"linux\":{}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"typo\":true}}")]
    [TestCase("\"runtime\":{\"tailscale\":{\"typo\":true}}")]
    [TestCase("\"runtime\":{\"singbox_api\":{\"port\":9090}}")]
    [TestCase("\"policies\":{\"typo\":true}")]
    [TestCase("\"runtime\":null")]
    [TestCase("\"runtime\":{\"tailscale\":{\"enabled\":null}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"connect_timeout\":null}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"tun_addresses\":null}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"address_family\":null}}")]
    [TestCase("\"policies\":{\"sniff_excluded_ports\":null}")]
    [TestCase("\"policies\":{\"sniff_timeout\":\"0s\"}")]
    [TestCase("\"policies\":{\"dns_timeout\":\"-1s\"}")]
    [TestCase("\"policies\":{\"url_test_idle_timeout\":\"forever\"}")]
    [TestCase("\"platforms\":{\"Linux\":{\"connect_timeout\":\"1w\"}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"tcp_keep_alive\":\"0s\"}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"tun_addresses\":[\"not-cidr\",\"fd00::1/126\"]}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"tun_addresses\":[\"172.19.0.1/33\",\"fd00::1/126\"]}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"tun_addresses\":[\"172.19.0.1/30\"]}}")]
    [TestCase("\"platforms\":{\"Linux\":{\"tun_addresses\":[\"fd00::1/126\"]}}")]
    [TestCase("\"platforms\":{\"Windows\":{\"multi_queue\":true}}")]
    [TestCase("\"platforms\":{\"Android\":{\"multi_queue\":true}}")]
    [TestCase("\"policies\":{\"url_test_url\":\"\"}")]
    [TestCase("\"policies\":{\"url_test_url\":\"file:///tmp/probe\"}")]
    [TestCase("\"policies\":{\"main_default_region\":\"missing\"}")]
    [TestCase("\"policies\":{\"dns_cache_capacity\":0}")]
    [TestCase("\"policies\":{\"url_test_tolerance\":-1}")]
    [TestCase("\"policies\":{\"sniff_excluded_ports\":[0]}")]
    [TestCase("\"policies\":{\"sniff_excluded_ports\":[22,22]}")]
    public void RejectsInvalidLayeredSettings(string fields) =>
        Assert.Throws<FormatException>(() => Load("{\"version\":1," + fields + "}"));

    [Test]
    public void ResolvedOutputSerializesTheEffectiveSnapshotDirectly()
    {
        var profile = Load("""
            {"version":1,"runtime":{"tailscale":{"enabled":false},"singbox_api":{"enabled":true}},
             "policies":{"sniff_timeout":"500ms","dns_cache_capacity":8192},
             "platforms":{"Linux":{"connect_timeout":"8s","tcp_keep_alive":null}}}
            """);
        using var json = JsonDocument.Parse(profile.ToResolvedJson());
        var root = json.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("runtime").GetProperty("tailscale").GetProperty("enabled").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("runtime").GetProperty("tailscale").GetProperty("android_enabled").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("runtime").GetProperty("singbox_api").GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("policies").GetProperty("sniff_timeout").GetString(), Is.EqualTo("500ms"));
            Assert.That(root.GetProperty("policies").GetProperty("dns_cache_capacity").GetUInt32(), Is.EqualTo(8192));
            Assert.That(root.GetProperty("platforms").GetProperty("Linux").GetProperty("connect_timeout").GetString(), Is.EqualTo("8s"));
            Assert.That(root.GetProperty("platforms").GetProperty("Linux").GetProperty("tcp_keep_alive").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"version\":\"1\"}")]
    [TestCase("{\"version\":null}")]
    [TestCase("{\"version\":2}")]
    [TestCase("{\"version\":1,\"version\":1}")]
    [TestCase("{\"version\":1,\"resolvers\":{}}")]
    [TestCase("{\"version\":1,\"services\":null}")]
    [TestCase("{\"version\":1,\"services\":{}}")]
    [TestCase("{\"version\":1,\"services\":[null]}")]
    [TestCase("{\"version\":1,\"regions\":null}")]
    [TestCase("{\"version\":1,\"regions\":{}}")]
    [TestCase("{\"version\":1,\"regions\":[null]}")]
    public void RejectsMalformedRoot(string json) => Assert.Throws<FormatException>(() => Load(json));

    [TestCase("{\"version\":1,\"\\uD800\":true}")]
    [TestCase("{\"version\":1,\"\\uDC00\":true}")]
    [TestCase("{\"version\":1,\"policies\":{\"url_test_url\":\"\\uD800\"}}")]
    [TestCase("{\"version\":1,\"policies\":{\"url_test_url\":\"\\uDC00\"}}")]
    public void RejectsUnpairedSurrogatesInProfileFiles(string json) =>
        Assert.Throws<FormatException>(() => Load(json));

    [Test]
    public void EscapedSurrogatePairsPreserveSupplementaryCharacters()
    {
        var profile = Load("""{"version":1,"regions":[{"id":"us","display_name":"\uD83D\uDE80 USA"}]}""");
        Assert.That(profile.Regions.Single(region => region.Id == "us").DisplayName, Is.EqualTo("🚀 USA"));
    }

    [TestCase("{}")]
    [TestCase("{\"id\":null}")]
    [TestCase("{\"id\":123}")]
    [TestCase("{\"id\":\"us\",\"id\":\"us\"}")]
    [TestCase("{\"id\":\"us\",\"typo\":true}")]
    [TestCase("{\"id\":\"us\",\"enabled\":false}")]
    [TestCase("{\"id\":\"us\",\"pattern\":null}")]
    [TestCase("{\"id\":\"us\",\"display_name\":null}")]
    [TestCase("{\"id\":\"us\",\"pattern\":\"\"}")]
    [TestCase("{\"id\":\"us\",\"pattern\":\"(\"}")]
    [TestCase("{\"id\":\"tw\"}")]
    [TestCase("{\"id\":\"tw\",\"display_name\":\"TW\"}")]
    [TestCase("{\"id\":\"tw\",\"pattern\":\"taiwan\"}")]
    [TestCase("{\"id\":\"TW\",\"display_name\":\"TW\",\"pattern\":\"taiwan\"}")]
    [TestCase("{\"id\":\"../tw\",\"display_name\":\"TW\",\"pattern\":\"taiwan\"}")]
    [TestCase("{\"id\":\"us\",\"display_name\":\"DIRECT\"}")]
    [TestCase("{\"id\":\"us\",\"display_name\":\"🤖 AI\"}")]
    [TestCase("{\"id\":\"us\",\"display_name\":\"🇯🇵 JP AUTO\"}")]
    [TestCase("{\"id\":\"us\",\"display_name\":\"response-global-primary-a\"}")]
    [TestCase("{\"id\":\"us\",\"display_name\":\"google\"}")]
    [TestCase("{\"id\":\"us\",\"display_name\":\" US\"}")]
    [TestCase("{\"id\":\"us\",\"display_name\":\"US\\n\"}")]
    public void RejectsInvalidRegions(string region) =>
        Assert.Throws<FormatException>(() => Load($"{{\"version\":1,\"regions\":[{region}]}}"));

    [Test]
    public void RejectsDuplicateIdsInsteadOfApplyingLastEntry() => Assert.Throws<FormatException>(() => Load(
        """{"version":1,"regions":[{"id":"us"},{"id":"us"}]}"""));

    [TestCase("{}")]
    [TestCase("{\"id\":null}")]
    [TestCase("{\"id\":123}")]
    [TestCase("{\"id\":\"AI\"}")]
    [TestCase("{\"id\":\"ai\",\"id\":\"ai\"}")]
    [TestCase("{\"id\":\"ai\",\"enabled\":false}")]
    [TestCase("{\"id\":\"ai\",\"before_domestic\":true}")]
    [TestCase("{\"id\":\"ai\",\"name\":null}")]
    [TestCase("{\"id\":\"ai\",\"name\":\"\"}")]
    [TestCase("{\"id\":\"ai\",\"name\":\"DIRECT\"}")]
    [TestCase("{\"id\":\"ai\",\"name\":\"google\"}")]
    [TestCase("{\"id\":\"ai\",\"name\":\"🇯🇵 JP AUTO\"}")]
    [TestCase("{\"id\":\"ai\",\"name\":\"🎵 Spotify\"}")]
    [TestCase("{\"id\":\"ai\",\"name\":\"response-global-primary-a\"}")]
    [TestCase("{\"id\":\"ai\",\"name\":\" AI\"}")]
    [TestCase("{\"id\":\"ai\",\"name\":\"AI\\n\"}")]
    [TestCase("{\"id\":\"ai\",\"default_region\":null}")]
    [TestCase("{\"id\":\"ai\",\"default_region\":\"missing\"}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":null}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":[]}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":[null]}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":[1]}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":[\"missing\"]}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":[\"🇺🇸 US\"]}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":[\"🤖 AI\"]}")]
    [TestCase("{\"id\":\"ai\",\"rule_sets\":[\"ai\",\"ai\"]}")]
    [TestCase("{\"id\":\"ai\",\"precedes_domestic_routes\":null}")]
    [TestCase("{\"id\":\"ai\",\"precedes_domestic_routes\":1}")]
    [TestCase("{\"id\":\"ai\",\"precedes_domestic_routes\":\"true\"}")]
    [TestCase("{\"id\":\"custom\"}")]
    [TestCase("{\"id\":\"custom\",\"name\":\"Custom\"}")]
    [TestCase("{\"id\":\"custom\",\"rule_sets\":[\"games\"]}")]
    public void RejectsInvalidServices(string service) =>
        Assert.Throws<FormatException>(() => Load($"{{\"version\":1,\"services\":[{service}]}}"));

    [Test]
    public void RejectsDuplicateServiceIds() => Assert.Throws<FormatException>(() => Load(
        """{"version":1,"services":[{"id":"ai"},{"id":"ai"}]}"""));

    [TestCase(TargetPlatform.Android, false)]
    [TestCase(TargetPlatform.Android, true)]
    [TestCase(TargetPlatform.Linux, false)]
    [TestCase(TargetPlatform.Linux, true)]
    [TestCase(TargetPlatform.Windows, false)]
    [TestCase(TargetPlatform.Windows, true)]
    public async Task ServiceOverridesSelectTaiwanAndStayInsideFixedRouteStages(TargetPlatform platform, bool precedesDomestic)
    {
        string path = Path.Combine(directory, "services.json");
        await File.WriteAllTextAsync(path, """
            {"version":1,"regions":[{"id":"tw","display_name":"🇹🇼 TW","pattern":"TW[0-9]+"}],
             "services":[{"id":"ai","name":"AI Taiwan","default_region":"tw"},
                         {"id":"custom","name":"Custom Games","rule_sets":["games"],"default_region":"tw",
                          "precedes_domestic_routes":PRECEDES}]}
            """.Replace("PRECEDES", precedesDomestic ? "true" : "false", StringComparison.Ordinal));
        using var provider = CreateProvider(path);
        var profile = provider.GetRequiredService<BoxForgeProfile>();
        Assert.That(profile.Services.Select(service => service.Id),
            Is.EqualTo(new[] { "ai", "google", "spotify", "games", "microsoft", "custom" }));
        Assert.That(profile.Services[0].RuleSets, Is.EqualTo(new[] { "ai" }));
        Assert.That(profile.Services[0].PrecedesDomesticRoutes, Is.True);
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("services", Yaml("TW01", "TW02"), [platform]));
        using var json = JsonDocument.Parse(bundle.Artifacts.Single().Content);
        var outbounds = json.RootElement.GetProperty("outbounds").EnumerateArray()
            .ToDictionary(item => item.GetProperty("tag").GetString()!);
        Assert.That(outbounds["AI Taiwan"].GetProperty("default").GetString(), Is.EqualTo("🇹🇼 TW"));
        Assert.That(outbounds["Custom Games"].GetProperty("default").GetString(), Is.EqualTo("🇹🇼 TW"));
        Assert.That(outbounds.ContainsKey(ServiceGroupNames.Ai), Is.False);
        var routePlan = new RouteProfileBuilder(profile).Plan(platform);
        Assert.That((precedesDomestic ? routePlan.PriorityServices : routePlan.OtherServices).Select(service => service.Name),
            Does.Contain("Custom Games"));
        Assert.That((precedesDomestic ? routePlan.OtherServices : routePlan.PriorityServices).Select(service => service.Name),
            Does.Not.Contain("Custom Games"));
        var rules = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToList();
        int lastGuard = rules.FindLastIndex(rule => rule.TryGetProperty("ip_version", out var version) && version.GetInt32() == 6);
        int customRoute = rules.FindIndex(rule => rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == "Custom Games");
        int domestic = rules.FindIndex(rule => rule.TryGetProperty("rule_set", out var sets) && sets[0].GetString() == "cn");
        Assert.That(customRoute, Is.GreaterThan(lastGuard));
        Assert.That(customRoute < domestic, Is.EqualTo(precedesDomestic));
        var firstGamesRoute = rules.First(rule => rule.TryGetProperty("outbound", out _)
            && rule.TryGetProperty("rule_set", out var sets) && sets[0].GetString() == "games");
        Assert.That(firstGamesRoute.GetProperty("outbound").GetString(),
            Is.EqualTo(precedesDomestic ? "Custom Games" : ServiceGroupNames.Games));
        Assert.That(rules.Count(rule => rule.TryGetProperty("ip_version", out var version) && version.GetInt32() == 6), Is.EqualTo(2));
        using var resolved = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.That(resolved.RootElement.GetProperty("services")[0].GetProperty("default_region").GetString(), Is.EqualTo("tw"));
    }

    [Test]
    public void ServiceRuleSetReplacementUpdatesDnsAndRouteTogether()
    {
        var profile = Load("""{"version":1,"services":[{"id":"ai","rule_sets":["cn"]}]}""");
        var dns = SingboxDnsEmitter.Emit(new DnsProfileBuilder(profile).Plan(TargetPlatform.Android));
        var route = SingboxRouteEmitter.Emit(new RouteProfileBuilder(profile).Plan(TargetPlatform.Android));
        Assert.That(dns.Rules.First(rule => rule.Action == DnsRuleAction.Evaluate).RuleSet,
            Is.EqualTo(new[] { "cn", "google", "spotify", "games", "microsoft" }));
        Assert.That(route.Rules.Single(rule => rule.Outbound == ServiceGroupNames.Ai).RuleSet, Is.EqualTo(new[] { "cn" }));
        Assert.That(BoxForgeProfile.Default.Services[0].RuleSets, Is.EqualTo(new[] { "ai" }));
        var newService = Load("""{"version":1,"services":[{"id":"custom","name":"Custom","rule_sets":["games"]}]}""");
        Assert.That(Plan(new NodeCatalog([], []), newService).ServiceOutbounds.Last().Default,
            Is.EqualTo(SingboxTags.MainProxyGroup));
        Assert.That(newService.Services.Last().PrecedesDomesticRoutes, Is.False);
    }

    [Test]
    public async Task EffectiveServiceNamesAreReservedWithoutChangingOtherProviders()
    {
        string path = Path.Combine(directory, "services.json");
        await File.WriteAllTextAsync(path, """{"version":1,"services":[{"id":"ai","name":"Custom AI"}]}""");
        using var custom = CreateProvider(path);
        using var defaults = CreateProvider(null);
        var engine = custom.GetRequiredService<IBoxForgeEngine>();
        Assert.ThrowsAsync<BoxForgeConversionException>(() => engine.ConvertAsync(
            new ConversionRequest("collision", Yaml("Custom AI"), [TargetPlatform.Linux])));
        Assert.DoesNotThrowAsync(async () => await defaults.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("valid", Yaml("Custom AI"), [TargetPlatform.Linux])));
        Assert.DoesNotThrowAsync(async () => await engine.ConvertAsync(
            new ConversionRequest("freed-name", Yaml(ServiceGroupNames.Ai), [TargetPlatform.Linux])));
    }

    [TestCase("ai")]
    [TestCase("google")]
    public async Task RuleSetSourceOverridesChangeOnlySelectedDownload(string tag)
    {
        string path = Path.Combine(directory, "sources.json");
        await File.WriteAllTextAsync(path,
            $$"""{"version":1,"rule_sets":[{"tag":"{{tag}}","url":"https://rules.example.net/{tag}.srs","update_interval":"12h"}]}""");
        using var custom = CreateProvider(path);
        using var defaults = CreateProvider(null);
        var request = new ConversionRequest("sources", Yaml("US01", "US02"), Enum.GetValues<TargetPlatform>());
        var actual = await custom.GetRequiredService<IBoxForgeEngine>().ConvertAsync(request);
        var expected = await defaults.GetRequiredService<IBoxForgeEngine>().ConvertAsync(request);
        foreach (var artifact in actual.Artifacts)
        {
            var root = JsonNode.Parse(artifact.Content)!;
            var baseline = JsonNode.Parse(expected.Artifacts.Single(item => item.Platform == artifact.Platform).Content)!;
            var sources = root["route"]!["rule_set"]!.AsArray();
            var downloads = sources.SelectMany(source =>
            {
                string[] tags = source!["tag"] is JsonArray tagsArray
                    ? tagsArray.Select(item => item!.GetValue<string>()).ToArray() : [source["tag"]!.GetValue<string>()];
                return tags.Select(id => (Id: id, Url: source["url"]!.GetValue<string>().Replace("{tag}", id, StringComparison.Ordinal),
                    Interval: source["update_interval"]!.GetValue<string>()));
            }).ToDictionary(item => item.Id, StringComparer.Ordinal);
            Assert.That(downloads, Has.Count.EqualTo(8));
            Assert.That(downloads[tag].Url, Is.EqualTo($"https://rules.example.net/{tag}.srs"));
            Assert.That(downloads[tag].Interval, Is.EqualTo("12h"));
            foreach (var source in BoxForgeProfile.Default.RuleSets)
                foreach (string other in source.Tags.Where(other => other != tag))
                {
                    Assert.That(downloads[other].Url, Is.EqualTo(source.Url.Replace("{tag}", other, StringComparison.Ordinal)));
                    Assert.That(downloads[other].Interval, Is.EqualTo(source.UpdateInterval));
                }
            Assert.That(sources.All(source => source!["type"]!.GetValue<string>() == "remote"
                && source["format"]!.GetValue<string>() == "binary" && source["http_client"] is null), Is.True);
            root["route"]!.AsObject().Remove("rule_set");
            baseline["route"]!.AsObject().Remove("rule_set");
            Assert.That(JsonNode.DeepEquals(root, baseline), Is.True, "Only route.rule_set may change");
        }
        using var resolved = JsonDocument.Parse(custom.GetRequiredService<BoxForgeProfile>()
            .ToResolvedJson());
        Assert.That(resolved.RootElement.GetProperty("rule_sets").EnumerateArray()
            .Single(source => source.GetProperty("tags").EnumerateArray().Any(value => value.GetString() == tag))
            .GetProperty("url").GetString(), Is.EqualTo($"https://rules.example.net/{tag}.srs"));
    }

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(5)]
    public void SplittingGroupedSourcesRetainsEveryTagAndInheritsUrls(int changedCount)
    {
        string[] group = ["ai", "spotify", "games", "cn", "cnip"];
        string[] changed = group.Take(changedCount).ToArray();
        var overrides = changed.Select(tag => new { tag, update_interval = "6h" });
        var profile = Load(JsonSerializer.Serialize(new { version = 1, rule_sets = overrides }));
        Assert.That(profile.RuleSets.SelectMany(source => source.Tags), Is.EquivalentTo(BoxForgeProfile.Default.RuleSets.SelectMany(source => source.Tags)));
        foreach (var source in profile.RuleSets.Where(source => source.Tags.Any(changed.Contains)))
        {
            Assert.That(source.Tags, Has.Length.EqualTo(1));
            Assert.That(source.Url, Is.EqualTo(BoxForgeProfile.Default.RuleSets[0].Url.Replace("{tag}", source.Tags[0], StringComparison.Ordinal)));
            Assert.That(source.UpdateInterval, Is.EqualTo("6h"));
        }
        if (changedCount == 4)
        {
            var remaining = profile.RuleSets.Single(source => source.Tags.Contains("cnip"));
            Assert.That(remaining.Tags, Has.Length.EqualTo(1));
            Assert.That(remaining.Url, Does.EndWith("cnip.srs"));
            Assert.That(remaining.UpdateInterval, Is.EqualTo("1d"));
        }
    }

    [Test]
    public void SourceTemplatesExpandEveryOccurrenceAndIgnoreOverrideEntryOrder()
    {
        var entries = new[]
        {
            new { tag = "cn", url = "https://rules.example.net/{tag}.srs?copy={tag}" },
            new { tag = "ai", url = "https://rules.example.net/{tag}.srs?copy={tag}" }
        };
        var profile = Load(JsonSerializer.Serialize(new { version = 1, rule_sets = entries }));
        var reversed = Load(JsonSerializer.Serialize(new { version = 1, rule_sets = entries.Reverse() }));
        Assert.That(profile.RuleSets.Single(source => source.Tags.Contains("ai")).Url,
            Is.EqualTo("https://rules.example.net/ai.srs?copy=ai"));
        Assert.That(profile.ToResolvedJson(),
            Is.EqualTo(reversed.ToResolvedJson()));
    }

    [TestCase("{\"version\":1,\"rule_sets\":[]}")]
    [TestCase("{\"version\":1,\"rule_sets\":[{\"tag\":\"ai\"}]}")]
    [TestCase("{\"version\":1,\"rule_sets\":[{\"tag\":\"ai\",\"update_interval\":\"1d\"}]}")]
    public void NoOpSourceOverridesPreserveGroupingAndSerializedOutput(string json)
    {
        var serializer = new BoxForge.Services.ConfigSerializer();
        var baseline = new SingboxConfig { Route = SingboxRouteEmitter.Emit(new RouteProfileBuilder(BoxForgeProfile.Default).Plan(TargetPlatform.Linux)) };
        var actual = new SingboxConfig { Route = SingboxRouteEmitter.Emit(new RouteProfileBuilder(Load(json)).Plan(TargetPlatform.Linux)) };
        Assert.That(serializer.Serialize(actual), Is.EqualTo(serializer.Serialize(baseline)));
    }

    [TestCase("1ns", "1ns")]
    [TestCase("1d", "1d")]
    [TestCase(".5d", "43200000000000ns")]
    [TestCase("1d12h", "1d12h")]
    [TestCase("+2h", "+2h")]
    [TestCase("9223372036854775807ns", "9223372036854775807ns")]
    [TestCase("106751.991167300645925925925924d", "9223372036854775807ns")]
    public void RuleSetIntervalsAcceptPositiveExtendedDurations(string interval, string expected)
    {
        var profile = Load(JsonSerializer.Serialize(new { version = 1, rule_sets = new[] { new { tag = "ai", update_interval = interval } } }));
        Assert.That(profile.RuleSets.Single(source => source.Tags.Contains("ai")).UpdateInterval, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("0s")]
    [TestCase("-1h")]
    [TestCase("3600")]
    [TestCase("1w")]
    [TestCase("1M")]
    [TestCase("1d ")]
    [TestCase("0.1ns")]
    [TestCase("9223372036854775808ns")]
    [TestCase("106752d")]
    public void RuleSetIntervalsRejectInvalidOrOutOfRangeValues(string interval) => Assert.Throws<FormatException>(() =>
        Load(JsonSerializer.Serialize(new { version = 1, rule_sets = new[] { new { tag = "ai", update_interval = interval } } })));

    [Test]
    public void DaysRemainInvalidForAnyTlsGoDurations() => Assert.Throws<NodeParseException>(() =>
        GoDurationHelper.ParsePositiveSecondsOrDuration("1d", "idle-session-check-interval"));

    [TestCase("/ai.srs")]
    [TestCase("http://rules.example.net/ai.srs")]
    [TestCase("file:///tmp/ai.srs")]
    [TestCase("https://")]
    [TestCase("https://user:pass@rules.example.net/ai.srs")]
    [TestCase("https://rules.example.net/ai.srs#fragment")]
    [TestCase("https://rules.example.net/ai file.srs")]
    [TestCase("https://rules.example.net/ai.srs\n")]
    [TestCase("https://rules.example.net/{unknown}.srs")]
    [TestCase("https://rules.example.net/{tag.srs")]
    [TestCase("https://{tag}.example.net/ai.srs")]
    [TestCase("https://rules.example.net:{tag}/ai.srs")]
    [TestCase("https://rules.example.net/%GG.srs")]
    public void RuleSetUrlsRejectInvalidOrUnsupportedUrls(string url) => Assert.Throws<FormatException>(() =>
        Load(JsonSerializer.Serialize(new { version = 1, rule_sets = new[] { new { tag = "ai", url } } })));

    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("[null]")]
    [TestCase("[{}]")]
    [TestCase("[{\"tag\":null}]")]
    [TestCase("[{\"tag\":\"missing\"}]")]
    [TestCase("[{\"tag\":\"ai\",\"tag\":\"ai\"}]")]
    [TestCase("[{\"tag\":\"ai\"},{\"tag\":\"ai\"}]")]
    [TestCase("[{\"tag\":\"ai\",\"url\":null}]")]
    [TestCase("[{\"tag\":\"ai\",\"update_interval\":null}]")]
    [TestCase("[{\"tag\":\"ai\",\"update_interval\":12}]")]
    [TestCase("[{\"tag\":\"ai\",\"format\":\"source\"}]")]
    [TestCase("[{\"tag\":\"ai\",\"http_client\":\"direct\"}]")]
    [TestCase("[{\"tag\":\"ai\",\"path\":\"file.srs\"}]")]
    public void RuleSetOverridesRejectMalformedEntries(string sources) => Assert.Throws<FormatException>(() =>
        Load($"{{\"version\":1,\"rule_sets\":{sources}}}"));

    [TestCase(TargetPlatform.Android, false)]
    [TestCase(TargetPlatform.Android, true)]
    [TestCase(TargetPlatform.Linux, false)]
    [TestCase(TargetPlatform.Linux, true)]
    [TestCase(TargetPlatform.Windows, false)]
    [TestCase(TargetPlatform.Windows, true)]
    public async Task NewRuleSetAndNetflixServiceGenerateCompleteReferences(TargetPlatform platform, bool priority)
    {
        string path = Path.Combine(directory, "netflix.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            rule_sets = new[] { new { tag = "netflix", url = "https://rules.example.net/{tag}.srs" } },
            services = new[] { new { id = "netflix", name = "Netflix", rule_sets = new[] { "netflix" },
                default_region = "us", precedes_domestic_routes = priority } }
        }));
        using var provider = CreateProvider(path);
        var profile = provider.GetRequiredService<BoxForgeProfile>();
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("netflix", Yaml("US01", "US02"), [platform]));
        using var document = JsonDocument.Parse(bundle.Artifacts.Single().Content);
        JsonElement root = document.RootElement;
        JsonElement route = root.GetProperty("route");
        JsonElement source = route.GetProperty("rule_set").EnumerateArray().Single(item =>
            item.GetProperty("tag").ValueKind == JsonValueKind.String && item.GetProperty("tag").GetString() == "netflix");
        Assert.Multiple(() =>
        {
            Assert.That(source.GetProperty("url").GetString(), Is.EqualTo("https://rules.example.net/netflix.srs"));
            Assert.That(source.GetProperty("type").GetString(), Is.EqualTo("remote"));
            Assert.That(source.GetProperty("format").GetString(), Is.EqualTo("binary"));
            Assert.That(source.GetProperty("update_interval").GetString(), Is.EqualTo("1d"));
            Assert.That(route.GetProperty("default_http_client").GetString(), Is.EqualTo(HttpClientTags.RuleSetProxy));
            JsonElement selector = root.GetProperty("outbounds").EnumerateArray()
                .Single(item => item.GetProperty("tag").GetString() == "Netflix");
            Assert.That(selector.GetProperty("default").GetString(), Is.EqualTo("🇺🇸 US"));
            JsonElement serviceRule = route.GetProperty("rules").EnumerateArray()
                .Single(rule => rule.TryGetProperty("outbound", out var tag) && tag.GetString() == "Netflix");
            Assert.That(serviceRule.GetProperty("rule_set").EnumerateArray().Select(tag => tag.GetString()),
                Is.EqualTo(new[] { "netflix" }));
            Assert.That(root.GetProperty("dns").GetProperty("rules").EnumerateArray().Any(rule =>
                rule.TryGetProperty("rule_set", out var tags) && tags.EnumerateArray().Any(tag => tag.GetString() == "netflix")), Is.True);
            var routePlan = new RouteProfileBuilder(profile).Plan(platform);
            Assert.That((priority ? routePlan.PriorityServices : routePlan.OtherServices).Select(service => service.Name),
                Does.Contain("Netflix"));
            Assert.That((priority ? routePlan.OtherServices : routePlan.PriorityServices).Select(service => service.Name),
                Does.Not.Contain("Netflix"));
            Assert.That(profile.IsReservedTag("netflix"), Is.True);
            Assert.That(BoxForgeProfile.Default.IsReservedTag("netflix"), Is.False);
        });
        Assert.ThrowsAsync<BoxForgeConversionException>(() => provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("collision", Yaml("netflix"), [platform])));
        using var resolved = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.That(resolved.RootElement.GetProperty("rule_sets").EnumerateArray().Any(item =>
            item.GetProperty("tags").EnumerateArray().Any(tag => tag.GetString() == "netflix")), Is.True);
    }

    [Test]
    public void NewRuleSetsAppendInFileOrderAlongsideExistingOverrides()
    {
        var profile = Load("""
            {"version":1,"rule_sets":[
              {"tag":"netflix","url":"https://rules.example.net/{tag}.srs"},
              {"tag":"ai","update_interval":"12h"},
              {"tag":"disney","url":"https://rules.example.net/{tag}.srs","update_interval":"6h"}
            ]}
            """);
        Assert.That(profile.RuleSets.TakeLast(2).Select(source => source.Tags.Single()), Is.EqualTo(new[] { "netflix", "disney" }));
        Assert.That(profile.RuleSets.Single(source => source.Tags.Contains("ai")).UpdateInterval, Is.EqualTo("12h"));
        Assert.That(profile.RuleSets.Single(source => source.Tags.Contains("disney")).UpdateInterval, Is.EqualTo("6h"));
        var google = profile.RuleSets.Single(source => source.Tags.Contains("google"));
        var defaultGoogle = BoxForgeProfile.Default.RuleSets.Single(source => source.Tags.Contains("google"));
        Assert.That(google.Tags, Is.EqualTo(defaultGoogle.Tags));
        Assert.That(google.Url, Is.EqualTo(defaultGoogle.Url));
        Assert.That(google.UpdateInterval, Is.EqualTo(defaultGoogle.UpdateInterval));
        Assert.That(BoxForgeProfile.Default.RuleSets.Any(source => source.Tags.Contains("netflix")), Is.False);
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("netflix ")]
    [TestCase("NetFlix")]
    [TestCase("netflix/path")]
    [TestCase("netflix_{tag}")]
    [TestCase("-netflix")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("bridge-out")]
    [TestCase("tun-in")]
    [TestCase("http-ruleset-proxy")]
    [TestCase("response-global-primary-a")]
    public void NewRuleSetTagsRejectInvalidNamesAndFixedTagCollisions(string tag) => Assert.Throws<FormatException>(() =>
        Load(JsonSerializer.Serialize(new { version = 1, rule_sets = new[] { new { tag, url = "https://rules.example.net/new.srs" } } })));

    [TestCase("regions", "{\"id\":\"us\",\"display_name\":\"netflix\"}")]
    [TestCase("services", "{\"id\":\"ai\",\"name\":\"netflix\"}")]
    public void NewRuleSetTagsCannotCollideWithEffectiveDisplayNames(string section, string item) => Assert.Throws<FormatException>(() =>
        Load($$"""{"version":1,"rule_sets":[{"tag":"netflix","url":"https://rules.example.net/new.srs"}],"{{section}}":[{{item}}]}"""));

    [TestCase("{\"tag\":\"netflix\"}")]
    [TestCase("{\"tag\":\"netflix\",\"update_interval\":\"1d\"}")]
    [TestCase("{\"tag\":\"netflix\",\"url\":\"http://rules.example.net/new.srs\"}")]
    [TestCase("{\"tag\":\"netflix\",\"url\":\"https://rules.example.net/new.srs\",\"update_interval\":\"0s\"}")]
    public void NewRuleSetsRequireValidUrlAndInterval(string entry) => Assert.Throws<FormatException>(() =>
        Load($$"""{"version":1,"rule_sets":[{{entry}}]}"""));

    [Test]
    public void NewRuleSetsStillRejectDuplicateTags() => Assert.Throws<FormatException>(() => Load("""
        {"version":1,"rule_sets":[
          {"tag":"netflix","url":"https://rules.example.net/first.srs"},
          {"tag":"netflix","url":"https://rules.example.net/second.srs"}
        ]}
        """));

    private static IEnumerable<TestCaseData> ResolverCases()
    {
        foreach (var (id, tag) in new[]
        {
            ("alidns", SingboxTags.DirectAliDns), ("tencent", SingboxTags.DirectTencentDns),
            ("cloudflare", SingboxTags.ProxyCloudflareDns), ("google", SingboxTags.ProxyGoogleDns)
        })
            foreach (var platform in Enum.GetValues<TargetPlatform>())
                yield return new TestCaseData(id, tag, platform);
    }

    [TestCaseSource(nameof(ResolverCases))]
    public async Task ResolverOverridesOnlyChangeSelectedDohAndItsBootstrapRoute(string id, string tag, TargetPlatform platform)
    {
        const string server = "203.0.113.53";
        const string serverName = "dns.example.net";
        string path = Path.Combine(directory, "resolvers.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            resolvers = new[] { new { id, server, server_name = serverName } }
        }));
        using var custom = CreateProvider(path);
        using var defaults = CreateProvider(null);
        var request = new ConversionRequest("resolver", Yaml("US01", "US02"), [platform]);
        var actual = await custom.GetRequiredService<IBoxForgeEngine>().ConvertAsync(request);
        var baseline = await defaults.GetRequiredService<IBoxForgeEngine>().ConvertAsync(request);
        var root = JsonNode.Parse(actual.Artifacts.Single().Content)!;
        var expected = JsonNode.Parse(baseline.Artifacts.Single().Content)!;
        var dns = expected["dns"]!["servers"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == tag)!;
        dns["server"] = server;
        dns["tls"]!["server_name"] = serverName;
        if (id == "alidns")
            foreach (var rule in expected["route"]!["rules"]!.AsArray()
                .Where(rule => rule!["ip_cidr"] is JsonArray cidrs && cidrs[0]!.GetValue<string>() == "223.5.5.5/32"))
                rule!["ip_cidr"]![0] = server + "/32";
        Assert.That(JsonNode.DeepEquals(root, expected), Is.True, "Only the selected DoH and primary bootstrap CIDR may change");
        var bootstrap = root["dns"]!["servers"]!.AsArray()
            .Single(item => item!["tag"]!.GetValue<string>() == SingboxTags.DirectAliDns)!;
        Assert.That(bootstrap["detour"], Is.Null, "Control-plane DNS must remain direct");
        Assert.That(bootstrap["tls"]!["enabled"]!.GetValue<bool>(), Is.True);
        Assert.That(root["route"]!["default_domain_resolver"]!["server"]!.GetValue<string>(), Is.EqualTo(SingboxTags.DirectAliDns));
        Assert.That(root["route"]!["default_domain_resolver"]!["strategy"]!.GetValue<string>(), Is.EqualTo("ipv4_only"));
        foreach (var client in root["http_clients"]!.AsArray())
            Assert.That(client!["domain_resolver"]!["server"]!.GetValue<string>(), Is.EqualTo(SingboxTags.DirectAliDns));
        Assert.That(defaults.GetRequiredService<BoxForgeProfile>().GetResolver(id).Server, Is.Not.EqualTo(server));
        using var resolved = JsonDocument.Parse(custom.GetRequiredService<BoxForgeProfile>()
            .ToResolvedJson());
        Assert.That(resolved.RootElement.GetProperty("resolvers").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id).GetProperty("server").GetString(), Is.EqualTo(server));
    }

    [Test]
    public void ResolverFieldsInheritIndependentlyAndKeepTheirOriginalOrder()
    {
        var profile = Load("""
            {"version":1,"resolvers":[{"id":"google","server_name":"dns.example.net"},
                                      {"id":"alidns","server":"192.0.2.53"}]}
            """);
        Assert.That(profile.GetResolver("google").Server, Is.EqualTo("8.8.8.8"));
        Assert.That(profile.GetResolver("alidns").ServerName, Is.EqualTo("dns.alidns.com"));
        using var resolved = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.That(resolved.RootElement.GetProperty("resolvers").EnumerateArray().Select(item => item.GetProperty("id").GetString()),
            Is.EqualTo(new[] { "alidns", "tencent", "cloudflare", "google" }));
    }

    [TestCase("{\"version\":1,\"resolvers\":[]}")]
    [TestCase("{\"version\":1,\"resolvers\":[{\"id\":\"alidns\"}]}")]
    [TestCase("{\"version\":1,\"resolvers\":[{\"id\":\"alidns\",\"server\":\"223.5.5.5\",\"server_name\":\"dns.alidns.com\"}]}")]
    public void NoOpResolverOverridesPreserveResolvedData(string json) => Assert.That(
        Load(json).ToResolvedJson(),
        Is.EqualTo(BoxForgeProfile.Default.ToResolvedJson()));

    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("[null]")]
    [TestCase("[{}]")]
    [TestCase("[{\"id\":null}]")]
    [TestCase("[{\"id\":\"quad9\"}]")]
    [TestCase("[{\"id\":\"ALIDNS\"}]")]
    [TestCase("[{\"id\":\"alidns\",\"id\":\"alidns\"}]")]
    [TestCase("[{\"id\":\"alidns\"},{\"id\":\"alidns\"}]")]
    [TestCase("[{\"id\":\"alidns\",\"server\":null}]")]
    [TestCase("[{\"id\":\"alidns\",\"server\":123}]")]
    [TestCase("[{\"id\":\"alidns\",\"server_name\":null}]")]
    [TestCase("[{\"id\":\"alidns\",\"detour\":\"PROXY\"}]")]
    [TestCase("[{\"id\":\"alidns\",\"insecure\":true}]")]
    [TestCase("[{\"id\":\"alidns\",\"type\":\"udp\"}]")]
    [TestCase("[{\"id\":\"alidns\",\"port\":53}]")]
    [TestCase("[{\"id\":\"alidns\",\"path\":\"/resolve\"}]")]
    [TestCase("[{\"id\":\"alidns\",\"primary\":\"google\"}]")]
    public void ResolverOverridesRejectMalformedEntries(string resolvers) => Assert.Throws<FormatException>(() =>
        Load($"{{\"version\":1,\"resolvers\":{resolvers}}}"));

    [TestCase("")]
    [TestCase("dns.example.net")]
    [TestCase("https://dns.example.net/dns-query")]
    [TestCase("::1")]
    [TestCase("::ffff:192.0.2.53")]
    [TestCase("192.0.2.53:443")]
    [TestCase("192.0.2.53/32")]
    [TestCase("192.0.2.53 ")]
    [TestCase("127.1")]
    [TestCase("2130706433")]
    [TestCase("001.2.3.4")]
    [TestCase("0.0.0.0")]
    [TestCase("0.1.2.3")]
    [TestCase("224.0.0.1")]
    [TestCase("255.255.255.255")]
    public void ResolverServersRequireCanonicalUnicastIpv4(string server) => Assert.Throws<FormatException>(() =>
        Load(JsonSerializer.Serialize(new { version = 1, resolvers = new[] { new { id = "alidns", server } } })));

    [TestCase("")]
    [TestCase("192.0.2.53")]
    [TestCase("https://dns.example.net")]
    [TestCase("dns.example.net:443")]
    [TestCase("*.example.net")]
    [TestCase("dns_example.net")]
    [TestCase("-dns.example.net")]
    [TestCase("dns-.example.net")]
    [TestCase("dns..example.net")]
    [TestCase("dns.example.net.")]
    [TestCase("dns.example.net\n")]
    [TestCase(" DNS.example.net")]
    [TestCase("解析.example.net")]
    public void ResolverTlsNamesRequireAsciiDnsNames(string serverName) => Assert.Throws<FormatException>(() =>
        Load(JsonSerializer.Serialize(new { version = 1, resolvers = new[] { new { id = "alidns", server_name = serverName } } })));

    [Test]
    public void ResolverTlsNamesRejectOverlongLabelsAndHostnames()
    {
        foreach (string name in new[] { new string('a', 64) + ".net",
            string.Join('.', new string('a', 63), new string('a', 63), new string('a', 63), new string('a', 62)) })
            Assert.Throws<FormatException>(() => Load(JsonSerializer.Serialize(new
            {
                version = 1,
                resolvers = new[] { new { id = "alidns", server_name = name } }
            })));
    }

    [TestCase("10.0.0.53", 63)]
    [TestCase("127.0.0.1", 253)]
    public void ResolverValidationAllowsLanLoopbackAndMaximumDnsNameLengths(string server, int length)
    {
        string name = length == 63 ? new string('a', 63)
            : string.Join('.', new string('a', 63), new string('a', 63), new string('a', 63), new string('a', 61));
        var profile = Load(JsonSerializer.Serialize(new
        {
            version = 1,
            resolvers = new[] { new { id = "alidns", server, server_name = name } }
        }));
        Assert.That(profile.GetResolver("alidns").Server, Is.EqualTo(server));
        Assert.That(profile.GetResolver("alidns").ServerName, Is.EqualTo(name));
    }

    [Test]
    public async Task ProvidersUseIndependentProfilesAndReserveEffectiveTags()
    {
        string path = Path.Combine(directory, "profile.json");
        await File.WriteAllTextAsync(path, TaiwanProfile);
        using var custom = CreateProvider(path);
        using var defaults = CreateProvider(null);
        var engine = custom.GetRequiredService<IBoxForgeEngine>();
        var result = await engine.ConvertAsync(new ConversionRequest("tw", Yaml("TW01", "TW02"), Enum.GetValues<TargetPlatform>()));
        foreach (var artifact in result.Artifacts)
        {
            using var json = JsonDocument.Parse(artifact.Content);
            var group = json.RootElement.GetProperty("outbounds").EnumerateArray()
                .Single(outbound => outbound.GetProperty("tag").GetString() == "🇹🇼 TW AUTO");
            Assert.That(group.GetProperty("outbounds").EnumerateArray().Select(value => value.GetString()),
                Is.EqualTo(new[] { "TW01", "TW02" }));
        }
        var baseline = await defaults.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("tw", Yaml("TW01", "TW02"), [TargetPlatform.Linux]));
        using var baselineJson = JsonDocument.Parse(baseline.Artifacts.Single().Content);
        Assert.That(baselineJson.RootElement.GetProperty("outbounds").EnumerateArray()
            .Select(outbound => outbound.GetProperty("tag").GetString()), Does.Not.Contain("🇹🇼 TW"));
        Assert.ThrowsAsync<BoxForgeConversionException>(() => engine.ConvertAsync(
            new ConversionRequest("collision", Yaml("🇹🇼 TW AUTO"), [TargetPlatform.Android])));
        Assert.DoesNotThrowAsync(async () => await defaults.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("valid-default", Yaml("🇹🇼 TW AUTO"), [TargetPlatform.Android])));
    }

    [Test]
    public void ResolvedOutputIncludesMergedDataAndEffectiveRuntimeSwitches()
    {
        var profile = Load(TaiwanProfile).ApplyOverrides(JsonNode.Parse("""
            {"version":1,"runtime":{"tailscale":{"enabled":false,"android_enabled":true},"singbox_api":{"enabled":true}}}
            """)!.AsObject());
        using var json = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.That(json.RootElement.GetProperty("regions").GetArrayLength(), Is.EqualTo(5));
        Assert.That(json.RootElement.GetProperty("runtime").GetProperty("tailscale").GetProperty("enabled").GetBoolean(), Is.False);
        Assert.That(json.RootElement.GetProperty("runtime").GetProperty("singbox_api").GetProperty("enabled").GetBoolean(), Is.True);
    }

    [Test]
    public async Task RegexTimeoutFailsConversionInsteadOfReturningPartialOutput()
    {
        string path = Path.Combine(directory, "timeout.json");
        await File.WriteAllTextAsync(path,
            """{"version":1,"regions":[{"id":"us","pattern":"^(a+)+$"}]}""");
        using var provider = CreateProvider(path);
        var error = Assert.ThrowsAsync<BoxForgePlatformConversionException>(() => provider.GetRequiredService<IBoxForgeEngine>()
            .ConvertAsync(new ConversionRequest("timeout", Yaml(new string('a', 10_000) + "!"), [TargetPlatform.Android])));
        Assert.That(error!.InnerException, Is.TypeOf<System.Text.RegularExpressions.RegexMatchTimeoutException>());
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task InjectedPlatformSettingsControlTheCompleteGenerationPath(TargetPlatform platform)
    {
        PlatformProfile original = BoxForgeProfile.Default.GetPlatform(platform);
        PlatformProfile settings = original with
        {
            AddressFamily = AddressFamilyPolicy.DualStack,
            StrictRoute = false,
            AutoRedirect = !original.UseDirectForwarding,
            MultiQueue = false,
            UseDirectForwarding = !original.UseDirectForwarding,
            TcpKeepAlive = "7m",
            TcpKeepAliveInterval = "17s",
            UrlTestInterval = "7m",
            DnsSelectionMode = original.DnsSelectionMode == DnsResolverSelectionMode.ParallelFastest
                ? DnsResolverSelectionMode.SequentialFallback : DnsResolverSelectionMode.ParallelFastest,
            TaildropDirectory = "CustomTaildrop"
        };
        var profile = new BoxForgeProfile(BuiltInProfile.Default with
        { Platforms = BuiltInProfile.Default.Platforms.SetItem(platform, settings) });
        using var provider = new ServiceCollection().AddLogging()
            .AddBoxForgeCore(new ConfigurationBuilder().Build()).AddSingleton(profile).BuildServiceProvider();
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("platform", Yaml("US01", "US02"), [platform]));
        using var document = JsonDocument.Parse(bundle.Artifacts.Single().Content);
        JsonElement root = document.RootElement;
        JsonElement tun = root.GetProperty("inbounds")[0];
        JsonElement[] outbounds = [.. root.GetProperty("outbounds").EnumerateArray()];
        JsonElement dns = root.GetProperty("dns");
        JsonElement route = root.GetProperty("route");

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetRequiredService<BoxForgeProfile>(), Is.SameAs(profile));
            Assert.That(profile.Platforms[platform], Is.SameAs(settings));
            Assert.That(tun.GetProperty("strict_route").GetBoolean(), Is.False);
            Assert.That(tun.GetProperty("auto_redirect").GetBoolean(), Is.EqualTo(settings.AutoRedirect));
            Assert.That(tun.GetProperty("multi_queue").GetBoolean(), Is.False);
            Assert.That(outbounds.Any(outbound => outbound.GetProperty("type").GetString() == "bridge"),
                Is.EqualTo(settings.UseDirectForwarding));
            Assert.That(route.GetProperty("rules").EnumerateArray().Any(rule =>
                    rule.TryGetProperty("action", out var action) && action.GetString() == "bypass"),
                Is.EqualTo(settings.UseDirectForwarding));
            foreach (JsonElement proxy in outbounds.Where(outbound => outbound.GetProperty("type").GetString() == "shadowsocks"))
            {
                Assert.That(proxy.GetProperty("tcp_keep_alive").GetString(), Is.EqualTo(settings.TcpKeepAlive));
                Assert.That(proxy.GetProperty("tcp_keep_alive_interval").GetString(), Is.EqualTo(settings.TcpKeepAliveInterval));
            }
            foreach (JsonElement auto in outbounds.Where(outbound => outbound.GetProperty("type").GetString() == "urltest"))
                Assert.That(auto.GetProperty("interval").GetString(), Is.EqualTo(settings.UrlTestInterval));
            Assert.That(dns.GetProperty("rules").EnumerateArray().Any(rule =>
                    rule.TryGetProperty("race", out var race) && race.GetBoolean()),
                Is.EqualTo(settings.DnsSelectionMode == DnsResolverSelectionMode.ParallelFastest));
            Assert.That(dns.TryGetProperty("strategy", out _), Is.False);
            Assert.That(dns.GetProperty("rules").EnumerateArray().Any(rule => rule.TryGetProperty("query_type", out var types)
                && types.EnumerateArray().Any(type => type.GetString() == "AAAA")), Is.False);
            Assert.That(route.GetProperty("rules").EnumerateArray().Any(rule => rule.TryGetProperty("ip_version", out _)), Is.False);
            Assert.That(route.GetProperty("default_domain_resolver").TryGetProperty("strategy", out _), Is.False);
            foreach (JsonElement client in root.GetProperty("http_clients").EnumerateArray())
                Assert.That(client.GetProperty("domain_resolver").TryGetProperty("strategy", out _), Is.False);
            Assert.That(root.GetProperty("endpoints")[0].GetProperty("taildrop_directory").GetString(),
                Is.EqualTo(settings.TaildropDirectory));
            Assert.That(BoxForgeProfile.Default.GetPlatform(platform), Is.SameAs(original));
        });
        using var resolved = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.That(resolved.RootElement.GetProperty("platforms").GetProperty(platform.ToString())
            .GetProperty("address_family").GetString(), Is.EqualTo("DualStack"));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task InjectedMobileClassificationKeepsTailscaleEndpointDnsAndRouteTogether(TargetPlatform platform)
    {
        PlatformProfile original = BoxForgeProfile.Default.GetPlatform(platform);
        PlatformProfile settings = original with { IsMobile = !original.IsMobile };
        foreach (bool desktopEnabled in new[] { false, true })
        {
            var profile = new BoxForgeProfile(BuiltInProfile.Default with
            {
                Platforms = BuiltInProfile.Default.Platforms.SetItem(platform, settings),
                Runtime = new(new(desktopEnabled, !desktopEnabled), new(false))
            });
            using var provider = new ServiceCollection().AddLogging()
                .AddBoxForgeCore(new ConfigurationBuilder().Build()).AddSingleton(profile).BuildServiceProvider();
            var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
                new ConversionRequest("tailscale", Yaml("US01"), [platform]));
            using var document = JsonDocument.Parse(bundle.Artifacts.Single().Content);
            JsonElement root = document.RootElement;
            bool enabled = settings.IsMobile ? !desktopEnabled : desktopEnabled;
            Assert.Multiple(() =>
            {
                Assert.That(root.TryGetProperty("endpoints", out _), Is.EqualTo(enabled));
                Assert.That(root.GetProperty("dns").GetProperty("servers").EnumerateArray()
                    .Any(server => server.GetProperty("tag").GetString() == SingboxTags.TailscaleDns), Is.EqualTo(enabled));
                Assert.That(root.GetProperty("dns").GetProperty("rules").EnumerateArray()
                    .Any(rule => rule.TryGetProperty("server", out var server) && server.GetString() == SingboxTags.TailscaleDns),
                    Is.EqualTo(enabled));
                Assert.That(root.GetProperty("route").GetProperty("rules").EnumerateArray()
                    .Any(rule => rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == SingboxTags.TailscaleEndpoint),
                    Is.EqualTo(enabled));
            });
        }
        Assert.That(BoxForgeProfile.Default.GetPlatform(platform), Is.SameAs(original));
    }

    [Test]
    public async Task RequestProfileControlsNodeValidationAndEmissionWithoutChangingTheHost()
    {
        using var provider = CreateProvider(null);
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var overlay = JsonNode.Parse("""
            {"version":1,"regions":[{"id":"us","display_name":"Request US"}]}
            """)!.AsObject();
        Assert.ThrowsAsync<BoxForgeConversionException>(() => engine.ConvertAsync(
            new ConversionRequest("reserved", Yaml("Request US"), [TargetPlatform.Linux]) { Profile = overlay }));

        var request = new ConversionRequest("valid", Yaml("US01", "US02"), [TargetPlatform.Linux]);
        var customized = await engine.ConvertAsync(request with { Profile = overlay });
        var unchanged = await engine.ConvertAsync(request);
        using var custom = JsonDocument.Parse(customized.Artifacts.Single().Content);
        using var original = JsonDocument.Parse(unchanged.Artifacts.Single().Content);
        Assert.Multiple(() =>
        {
            Assert.That(custom.RootElement.GetProperty("outbounds").EnumerateArray()
                .Any(outbound => outbound.GetProperty("tag").GetString() == "Request US"), Is.True);
            Assert.That(original.RootElement.GetProperty("outbounds").EnumerateArray()
                .Any(outbound => outbound.GetProperty("tag").GetString() == "Request US"), Is.False);
        });
        Assert.DoesNotThrowAsync(() => engine.ConvertAsync(
            new ConversionRequest("host", Yaml("Request US"), [TargetPlatform.Linux])));
    }

    private static ProfilePlan Plan(NodeCatalog nodes, BoxForgeProfile profile) =>
        SingboxGroupEmitter.Emit(ProfilePlanner.PlanGroups(nodes, profile, probeInterval: null));

    private static BoxForgeProfile Load(string json)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return BoxForgeProfile.Load(source);
    }

    private static ServiceProvider CreateProvider(string? path, Dictionary<string, string?>? values = null)
    {
        var settings = values is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(values);
        settings["Profile"] = path;
        return new ServiceCollection().AddLogging().AddBoxForgeCore(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).BuildServiceProvider();
    }

    internal static string Yaml(params string[] names) => "proxies:\n" + string.Concat(names.Select(name =>
        $"  - name: {JsonSerializer.Serialize(name)}\n    type: ss\n    server: node.example.com\n    port: 443\n    cipher: aes-128-gcm\n    password: test-only\n"));
}
