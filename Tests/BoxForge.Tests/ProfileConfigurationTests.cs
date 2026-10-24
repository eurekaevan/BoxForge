using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxForge.Configuration;
using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProfileConfigurationTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory("boxforge-profile-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [TestCase("{}")]
    [TestCase("{\"features\":{},\"dns\":{},\"route\":{},\"tuning\":{},\"privacy\":{},\"platforms\":{}}")]
    [TestCase("{\"route\":{\"regions\":{\"append\":[],\"remove\":[]}}}")]
    public void EmptyOperationsPreserveDefaults(string fields)
    {
        var overlay = JsonNode.Parse(fields)!.AsObject();
        var profile = BoxForgeProfile.Default.ApplyOverrides(overlay);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(profile.ToResolvedJson()),
            JsonNode.Parse(BoxForgeProfile.Default.ToResolvedJson())), Is.True);
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task AutoAddressFamilyWorksThroughFilesEnvironmentAndRequestsWithoutChangingDefaults(TargetPlatform platform)
    {
        var overlay = JsonNode.Parse("""{"dns":{"address_family":"Auto"}}""")!.AsObject();
        string path = Path.Combine(directory, "auto.json");
        File.WriteAllText(path, overlay.ToJsonString());
        var environment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["dns:address_family"] = "Auto"
        }).Build();
        foreach (BoxForgeProfile profile in new[]
        {
            BoxForgeProfile.Load(path),
            ProfileConfiguration.ResolveProfile(environment)
        })
        {
            using var document = await Generate(profile, platform, Yaml("US01"));
            JsonElement root = document.RootElement;
            JsonElement dns = root.GetProperty("dns");
            Assert.Multiple(() =>
            {
                Assert.That(profile.Resolve(platform).Settings.AddressFamily, Is.EqualTo(AddressFamilyPolicy.Auto));
                Assert.That(dns.TryGetProperty("strategy", out _), Is.False);
                Assert.That(dns.GetProperty("rules")[0].GetProperty("rules")[1]
                    .GetProperty("default_interface_address")[0].GetString(), Is.EqualTo("2000::/3"));
                Assert.That(root.GetProperty("inbounds")[0].GetProperty("address").EnumerateArray()
                    .Select(value => value.GetString()).Any(value => value!.Contains(':')), Is.True);
            });
        }
        using var provider = CreateProvider(null);
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var request = new ConversionRequest("auto", Yaml("US01"), [platform]);
        var custom = await engine.ConvertAsync(request with
        {
            Profile = JsonNode.Parse("""{"dns":{"address_family":"Ipv4Only"}}""")!.AsObject()
        });
        var explicitAuto = await engine.ConvertAsync(request with { Profile = overlay });
        var explicitDefault = await engine.ConvertAsync(request with
        {
            Profile = JsonNode.Parse("""{"dns":{"address_family":"DirectDualStack"}}""")!.AsObject()
        });
        var ordinary = await engine.ConvertAsync(request);
        using var customJson = JsonDocument.Parse(custom.Artifacts.Single().Content);
        using var ordinaryJson = JsonDocument.Parse(ordinary.Artifacts.Single().Content);
        Assert.That(explicitDefault.Artifacts.Single().Content, Is.EqualTo(ordinary.Artifacts.Single().Content));
        Assert.That(explicitAuto.Artifacts.Single().Content, Is.Not.EqualTo(ordinary.Artifacts.Single().Content));
        Assert.Multiple(() =>
        {
            Assert.That(customJson.RootElement.GetProperty("dns").GetProperty("strategy").GetString(), Is.EqualTo("ipv4_only"));
            Assert.That(ordinaryJson.RootElement.GetProperty("dns").TryGetProperty("strategy", out _), Is.False);
            Assert.That(ordinaryJson.RootElement.GetProperty("dns").GetProperty("rules")[0]
                .GetProperty("rules")[1].GetProperty("default_interface_address")[0].GetString(), Is.EqualTo("2000::/3"));
            Assert.That(BoxForgeProfile.Default.Resolve(platform).Settings.AddressFamily, Is.EqualTo(AddressFamilyPolicy.DirectDualStack));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task AutoRetainsExplicitNodeIpv4ResolutionWithoutConstrainingOtherNodes(TargetPlatform platform)
    {
        var profile = Load("""{"dns":{"address_family":"Auto"}}""");
        string yaml = Yaml("constrained") + "    ip-version: ipv4\n" + Yaml("unconstrained")["proxies:\n".Length..];
        using var document = await Generate(profile, platform, yaml);
        JsonElement[] proxies = document.RootElement.GetProperty("outbounds").EnumerateArray()
            .Where(node => node.TryGetProperty("server", out _)).ToArray();
        JsonElement constrained = proxies.Single(node => node.GetProperty("tag").GetString() == "constrained");
        JsonElement unconstrained = proxies.Single(node => node.GetProperty("tag").GetString() == "unconstrained");
        Assert.Multiple(() =>
        {
            Assert.That(constrained.GetProperty("domain_resolver").GetProperty("strategy").GetString(), Is.EqualTo("ipv4_only"));
            Assert.That(unconstrained.TryGetProperty("domain_resolver", out _), Is.False);
        });
    }

    [Test]
    public void RootPlatformAndRequestLayersApplyInOrderWithoutMutatingTheirInputs()
    {
        var overlay = JsonNode.Parse("""
            {"tuning":{"connect_timeout":"7s"},
             "platforms":{"Linux":{"tuning":{"connect_timeout":"9s"}}}}
            """)!.AsObject();
        string input = overlay.ToJsonString();
        string defaults = BoxForgeProfile.Default.ToResolvedJson();
        var profile = BoxForgeProfile.Default.ApplyOverrides(overlay);
        var request = JsonNode.Parse("""
            {"tuning":{"connect_timeout":"11s"},
             "platforms":{"Linux":{"tuning":{"connect_timeout":"13s"}}}}
            """)!.AsObject();
        string requestInput = request.ToJsonString();
        ResolvedProfile resolved = profile.Resolve(TargetPlatform.Linux, request);
        string resolvedSnapshot = resolved.ToResolvedJson();
        Assert.Multiple(() =>
        {
            Assert.That(profile.Resolve(TargetPlatform.Windows).Tuning.ConnectTimeout, Is.EqualTo("7s"));
            Assert.That(profile.Resolve(TargetPlatform.Linux).Tuning.ConnectTimeout, Is.EqualTo("9s"));
            Assert.That(profile.Resolve(TargetPlatform.Linux, request).Tuning.ConnectTimeout, Is.EqualTo("13s"));
            Assert.That(profile.Resolve(TargetPlatform.Windows, request).Tuning.ConnectTimeout, Is.EqualTo("11s"));
            Assert.That(profile.Resolve(TargetPlatform.Linux).Tuning.ConnectTimeout, Is.EqualTo("9s"));
            Assert.That(overlay.ToJsonString(), Is.EqualTo(input));
            Assert.That(request.ToJsonString(), Is.EqualTo(requestInput));
            Assert.That(BoxForgeProfile.Default.ToResolvedJson(), Is.EqualTo(defaults));
        });
        overlay["tuning"]!["connect_timeout"] = "19s";
        request["tuning"]!["connect_timeout"] = "23s";
        request["platforms"]!["Linux"]!["tuning"]!["connect_timeout"] = "29s";
        Assert.Multiple(() =>
        {
            Assert.That(profile.Resolve(TargetPlatform.Linux).Tuning.ConnectTimeout, Is.EqualTo("9s"));
            Assert.That(resolved.Platform, Is.EqualTo(TargetPlatform.Linux));
            Assert.That(resolved.Settings.ConnectTimeout, Is.EqualTo("13s"));
            Assert.That(resolved.ToResolvedJson(), Is.EqualTo(resolvedSnapshot));
        });
    }

    [Test]
    public void RequestsRejectUnknownFieldsInUnselectedPlatformLayers()
    {
        var request = JsonNode.Parse("""
            {"platforms":{"Windows":{"typo":true}}}
            """)!.AsObject();
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.Resolve(TargetPlatform.Linux, request));
    }

    [Test]
    public void RequestsRejectNullElementsInsideUnselectedPlatformEntities()
    {
        var request = JsonNode.Parse("""
            {"platforms":{"Windows":{"route":{"rule_sets":{"append":[
              {"tags":[null],"url":"https://rules.example.net/custom.srs","update_interval":"1d"}
            ]}}}}}
            """)!.AsObject();
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.Resolve(TargetPlatform.Linux, request));
    }

    [Test]
    public async Task PlatformNullableValuesCanBeClearedWithoutChangingOtherPlatforms()
    {
        var profile = Load("""
            {"platforms":{"Linux":{
                "route":{"strict_route":null,"auto_redirect":null,"multi_queue":null},
                "tuning":{"tcp_keep_alive":null,"tcp_keep_alive_interval":null}}}}
            """);
        var effective = profile.Resolve(TargetPlatform.Linux);
        Assert.Multiple(() =>
        {
            Assert.That(effective.Settings.StrictRoute, Is.Null);
            Assert.That(effective.Settings.AutoRedirect, Is.Null);
            Assert.That(effective.Settings.MultiQueue, Is.Null);
            Assert.That(effective.Tuning.TcpKeepAlive, Is.Null);
            Assert.That(effective.Tuning.TcpKeepAliveInterval, Is.Null);
            Assert.That(profile.Resolve(TargetPlatform.Windows).Settings.StrictRoute, Is.True);
            Assert.That(BoxForgeProfile.Default.Resolve(TargetPlatform.Linux).Settings.MultiQueue, Is.True);
        });
        using var document = await Generate(profile, TargetPlatform.Linux, Yaml("US01"));
        JsonElement tun = document.RootElement.GetProperty("inbounds")[0];
        Assert.Multiple(() =>
        {
            Assert.That(tun.TryGetProperty("strict_route", out _), Is.False);
            Assert.That(tun.TryGetProperty("auto_redirect", out _), Is.False);
            Assert.That(tun.TryGetProperty("multi_queue", out _), Is.False);
        });
    }

    [Test]
    public void FileAndUnifiedHostValuesPrecedePlatformOverrides()
    {
        string path = Path.Combine(directory, "profile.json");
        File.WriteAllText(path, """
            {"tuning":{"connect_timeout":"7s"},
             "platforms":{"Linux":{"tuning":{"connect_timeout":"9s"}}}}
            """);
        using var provider = CreateProvider(path, new() { ["tuning:connect_timeout"] = "11s" });
        var profile = provider.GetRequiredService<BoxForgeProfile>();
        Assert.Multiple(() =>
        {
            Assert.That(profile.Resolve(TargetPlatform.Windows).Tuning.ConnectTimeout, Is.EqualTo("11s"));
            Assert.That(profile.Resolve(TargetPlatform.Linux).Tuning.ConnectTimeout, Is.EqualTo("9s"));
        });
    }

    [Test]
    public void ArraysApplyReplaceAppendRemoveInFixedOrderAndCompareWholeValues()
    {
        var profile = Load("""
            {"route":{"sniff_excluded_ports":{
                "remove":[25],"append":[465],"replace":[25,587]}}}
            """);
        Assert.That(Resolved(profile)["route"]!["sniff_excluded_ports"]!.AsArray().Select(value => value!.GetValue<int>()),
            Is.EqualTo(new[] { 587, 465 }));
        var item = JsonNode.Parse("""{"id":"tw","display_name":"TW","pattern":"taiwan"}""")!;
        var added = BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "regions", "append", item));
        var equalValue = JsonNode.Parse("""{"pattern":"taiwan","display_name":"TW","id":"tw"}""")!;
        var removed = added.ApplyOverrides(ArrayOperation("route", "regions", "remove", equalValue));
        Assert.That(removed.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Id), Is.EqualTo(BoxForgeProfile.Default.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Id)));
        var changedValue = JsonNode.Parse("""{"id":"tw","display_name":"Changed","pattern":"taiwan"}""")!;
        Assert.Throws<ProfileValidationException>(() => added.ApplyOverrides(ArrayOperation("route", "regions", "remove", changedValue)));
    }

    [TestCase("https://rules.example.net/{tag}.srs", "1d")]
    [TestCase("https://rules.example.net/custom.srs", ".5d")]
    public async Task PlatformRuleSetRemovalMatchesRawValuesInFilesOverridesAndRequests(string url, string interval)
    {
        var source = new JsonObject
        {
            ["tags"] = new JsonArray("custom"),
            ["url"] = url,
            ["update_interval"] = interval
        };
        var overlay = ArrayOperation("route", "rule_sets", "append", source);
        overlay["platforms"] = new JsonObject
        {
            ["Linux"] = new JsonObject
            {
                ["route"] = new JsonObject
                { ["rule_sets"] = new JsonObject { ["remove"] = new JsonArray(source.DeepClone()) } }
            }
        };
        string path = Path.Combine(directory, "remove-source.json");
        await File.WriteAllTextAsync(path, overlay.ToJsonString());
        var fromFile = BoxForgeProfile.Load(path);
        var fromOverride = BoxForgeProfile.Default.ApplyOverrides(overlay);
        var fromRequest = BoxForgeProfile.Default.Resolve(TargetPlatform.Linux, overlay);
        var expected = Resolved(BoxForgeProfile.Default.Resolve(TargetPlatform.Linux));
        foreach (var profile in new[] { fromFile.Resolve(TargetPlatform.Linux), fromOverride.Resolve(TargetPlatform.Linux), fromRequest })
            Assert.That(JsonNode.DeepEquals(Resolved(profile), expected), Is.True);
        Assert.That(Resolved(fromFile)["route"]!["rule_sets"]!.AsArray().Any(item => JsonNode.DeepEquals(item, source)), Is.True);

        string yaml = Yaml("US01");
        using var expectedConfig = await Generate(BoxForgeProfile.Default, TargetPlatform.Linux, yaml);
        foreach (var profile in new[] { fromFile, fromOverride })
        {
            using var actual = await Generate(profile, TargetPlatform.Linux, yaml);
            Assert.That(actual.RootElement.GetRawText(), Is.EqualTo(expectedConfig.RootElement.GetRawText()));
        }
        using var provider = CreateProvider(null);
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("request", yaml, [TargetPlatform.Linux]) { Profile = overlay });
        using var requestConfig = JsonDocument.Parse(bundle.Artifacts.Single().Content);
        Assert.That(requestConfig.RootElement.GetRawText(), Is.EqualTo(expectedConfig.RootElement.GetRawText()));
    }

    [TestCase("https://rules.example.net/{tag}.srs", "1d")]
    [TestCase("https://rules.example.net/custom.srs", ".5d")]
    public void ChainedRuleSetRemovalMatchesTheOriginalCompleteEntity(string url, string interval)
    {
        var source = new JsonObject
        {
            ["tags"] = new JsonArray("custom"),
            ["url"] = url,
            ["update_interval"] = interval
        };
        var appended = BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "rule_sets", "append", source));
        var removed = appended.ApplyOverrides(ArrayOperation("route", "rule_sets", "remove", source));
        Assert.That(JsonNode.DeepEquals(Resolved(removed), Resolved(BoxForgeProfile.Default)), Is.True);
    }

    [Test]
    public void AppendDoesNotImplicitlyMergeEntitiesByIdAndReplacementPreservesOtherEntries()
    {
        JsonObject original = Entity("route", "regions", "us");
        var changed = (JsonObject)original.DeepClone();
        changed["display_name"] = "Custom US";
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "regions", "append", changed)));
        var profile = ReplaceEntity("route", "regions", "us", new JsonObject { ["display_name"] = "Custom US" });
        Assert.Multiple(() =>
        {
            Assert.That(profile.Resolve(TargetPlatform.Linux).Regions.Single(region => region.Id == "us").DisplayName, Is.EqualTo("Custom US"));
            Assert.That(profile.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Id), Is.EqualTo(BoxForgeProfile.Default.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Id)));
            Assert.That(profile.Resolve(TargetPlatform.Linux).Regions.Single(region => region.Id == "jp").DisplayName,
                Is.EqualTo(BoxForgeProfile.Default.Resolve(TargetPlatform.Linux).Regions.Single(region => region.Id == "jp").DisplayName));
        });
    }

    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("{\"replace\":null}")]
    [TestCase("{\"append\":{}}")]
    [TestCase("{\"remove\":25}")]
    [TestCase("{\"add\":[25]}")]
    [TestCase("{\"remove\":[65535]}")]
    [TestCase("{\"replace\":[25],\"remove\":[25,25]}")]
    [TestCase("{\"replace\":[25],\"replace\":[465]}")]
    public void RejectsInvalidArrayOperations(string operation) => Assert.Throws<ProfileValidationException>(() =>
        Load("{\"route\":{\"sniff_excluded_ports\":" + operation + "}}"));

    [TestCase("{\"features\":{\"tailscale\":{\"typo\":true}}}")]
    [TestCase("{\"features\":{\"api\":{\"port\":9090}}}")]
    [TestCase("{\"privacy\":{\"enabled\":true}}")]
    [TestCase("{\"platforms\":{\"MacOS\":{}}}")]
    [TestCase("{\"platforms\":{\"linux\":{}}}")]
    [TestCase("{\"platforms\":{\"Linux\":{\"version\":1}}}")]
    [TestCase("{\"platforms\":{\"Linux\":{\"platforms\":{}}}}")]
    [TestCase("{\"features\":null}")]
    [TestCase("{\"features\":{\"tailscale\":{\"enabled\":null}}}")]
    [TestCase("{\"tuning\":{\"connect_timeout\":null}}")]
    [TestCase("{\"dns\":{\"address_family\":null}}")]
    [TestCase("{\"route\":{\"sniff_timeout\":\"0s\"}}")]
    [TestCase("{\"dns\":{\"timeout\":\"-1s\"}}")]
    [TestCase("{\"tuning\":{\"url_test_idle_timeout\":\"forever\"}}")]
    [TestCase("{\"tuning\":{\"connect_timeout\":\"1w\"}}")]
    [TestCase("{\"tuning\":{\"tcp_keep_alive\":\"0s\"}}")]
    [TestCase("{\"route\":{\"tun_addresses\":{\"replace\":[\"not-cidr\",\"fd00::1/126\"]}}}")]
    [TestCase("{\"route\":{\"tun_addresses\":{\"replace\":[\"172.19.0.1/33\",\"fd00::1/126\"]}}}")]
    [TestCase("{\"route\":{\"tun_addresses\":{\"replace\":[\"172.19.0.1/30\"]}}}")]
    [TestCase("{\"route\":{\"tun_addresses\":{\"replace\":[\"fd00::1/126\"]}}}")]
    [TestCase("{\"platforms\":{\"Windows\":{\"route\":{\"multi_queue\":true}}}}")]
    [TestCase("{\"platforms\":{\"Android\":{\"route\":{\"multi_queue\":true}}}}")]
    [TestCase("{\"tuning\":{\"url_test_url\":\"\"}}")]
    [TestCase("{\"tuning\":{\"url_test_url\":\"file:///tmp/probe\"}}")]
    [TestCase("{\"route\":{\"main_default_region\":\"missing\"}}")]
    [TestCase("{\"dns\":{\"cache_capacity\":0}}")]
    [TestCase("{\"tuning\":{\"url_test_tolerance\":-1}}")]
    [TestCase("{\"route\":{\"sniff_excluded_ports\":{\"replace\":[0]}}}")]
    [TestCase("{\"route\":{\"sniff_excluded_ports\":{\"replace\":[22,22]}}}")]
    public void RejectsInvalidLayeredSettings(string fields)
    {
        var overlay = JsonNode.Parse(fields)!.AsObject();
        Assert.Throws<ProfileValidationException>(() => Load(overlay.ToJsonString()));
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{")]
    [TestCase("{\"version\":1}")]
    [TestCase("{\"features\":{},\"features\":{}}")]
    [TestCase("{\"regions\":[]}")]
    [TestCase("{\"services\":[]}")]
    [TestCase("{\"rule_sets\":[]}")]
    [TestCase("{\"resolvers\":[]}")]
    [TestCase("{\"runtime\":{}}")]
    [TestCase("{\"policies\":{}}")]
    [TestCase("{\"platforms\":{\"Linux\":{\"connect_timeout\":\"9s\"}}}")]
    public void RejectsMalformedRootAndLegacySchema(string json) => Assert.Throws<ProfileValidationException>(() => Load(json));

    [TestCase("{\"\\uD800\":true}")]
    [TestCase("{\"\\uDC00\":true}")]
    [TestCase("{\"tuning\":{\"url_test_url\":\"\\uD800\"}}")]
    [TestCase("{\"tuning\":{\"url_test_url\":\"\\uDC00\"}}")]
    public void RejectsUnpairedSurrogatesInProfileFiles(string json) => Assert.Throws<ProfileValidationException>(() => Load(json));

    [Test]
    public void EscapedSurrogatePairsPreserveSupplementaryCharacters()
    {
        var profile = ReplaceEntity("route", "regions", "us", new JsonObject { ["display_name"] = "\U0001f680 US" });
        Assert.That(profile.Resolve(TargetPlatform.Linux).Regions.Single(region => region.Id == "us").DisplayName, Is.EqualTo("🚀 US"));
    }

    [TestCase("route", "regions", "append", "[null]")]
    [TestCase("route", "services", "append", "[null]")]
    [TestCase("route", "rule_sets", "append", "[null]")]
    [TestCase("dns", "resolvers", "replace", "[null]")]
    [TestCase("route", "tun_addresses", "replace", "[null,\"fd00::1/126\"]")]
    public void ArrayNullElementsProduceProfileErrors(string section, string field, string operation, string items) =>
        Assert.Throws<ProfileValidationException>(() => Load("{\"" + section + "\":{\"" + field
            + "\":{\"" + operation + "\":" + items + "}}}"));

    [TestCase("route", "regions", "{}")]
    [TestCase("route", "regions", "{\"id\":\"tw\"}")]
    [TestCase("route", "regions", "{\"id\":\"tw\",\"display_name\":\"TW\"}")]
    [TestCase("route", "services", "{}")]
    [TestCase("route", "services", "{\"id\":\"custom\",\"name\":\"Custom\"}")]
    [TestCase("route", "services", "{\"id\":\"custom\",\"name\":\"Custom\",\"rule_sets\":[\"games\"]}")]
    [TestCase("route", "rule_sets", "{}")]
    [TestCase("route", "rule_sets", "{\"tags\":[\"netflix\"]}")]
    [TestCase("route", "rule_sets", "{\"tags\":[\"netflix\"],\"url\":\"https://rules.example.net/new.srs\"}")]
    [TestCase("dns", "resolvers", "{}")]
    [TestCase("dns", "resolvers", "{\"id\":\"custom\"}")]
    [TestCase("dns", "resolvers", "{\"id\":\"custom\",\"server\":\"192.0.2.53\"}")]
    public void ArrayEntitiesMustBeComplete(string section, string field, string entity) => Assert.Throws<ProfileValidationException>(() =>
        Load("{\"" + section + "\":{\"" + field + "\":{\"append\":[" + entity + "]}}}"));

    [TestCase("id", "null")]
    [TestCase("id", "123")]
    [TestCase("id", "\"TW\"")]
    [TestCase("id", "\"../tw\"")]
    [TestCase("display_name", "null")]
    [TestCase("display_name", "\"\"")]
    [TestCase("display_name", "\"DIRECT\"")]
    [TestCase("display_name", "\"🤖 AI\"")]
    [TestCase("display_name", "\"🇯🇵 JP AUTO\"")]
    [TestCase("display_name", "\"response-global-primary-a\"")]
    [TestCase("display_name", "\"google\"")]
    [TestCase("display_name", "\" US\"")]
    [TestCase("display_name", "\"US\\n\"")]
    [TestCase("pattern", "null")]
    [TestCase("pattern", "\"\"")]
    [TestCase("pattern", "\"(\"")]
    [TestCase("typo", "true")]
    [TestCase("enabled", "false")]
    public void RejectsInvalidRegions(string field, string value) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceEntity("route", "regions", "us", new JsonObject { [field] = JsonNode.Parse(value) }));

    [TestCase("id", "null")]
    [TestCase("id", "123")]
    [TestCase("id", "\"AI\"")]
    [TestCase("name", "null")]
    [TestCase("name", "\"\"")]
    [TestCase("name", "\"DIRECT\"")]
    [TestCase("name", "\"google\"")]
    [TestCase("name", "\"🇯🇵 JP AUTO\"")]
    [TestCase("name", "\"🎵 Spotify\"")]
    [TestCase("name", "\"response-global-primary-a\"")]
    [TestCase("name", "\" AI\"")]
    [TestCase("name", "\"AI\\n\"")]
    [TestCase("default_region", "\"missing\"")]
    [TestCase("rule_sets", "null")]
    [TestCase("rule_sets", "[]")]
    [TestCase("rule_sets", "[null]")]
    [TestCase("rule_sets", "[1]")]
    [TestCase("rule_sets", "[\"missing\"]")]
    [TestCase("rule_sets", "[\"🇺🇸 US\"]")]
    [TestCase("rule_sets", "[\"🤖 AI\"]")]
    [TestCase("rule_sets", "[\"ai\",\"ai\"]")]
    [TestCase("precedes_domestic_routes", "null")]
    [TestCase("precedes_domestic_routes", "1")]
    [TestCase("precedes_domestic_routes", "\"true\"")]
    [TestCase("enabled", "false")]
    [TestCase("before_domestic", "true")]
    public void RejectsInvalidServices(string field, string value) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceEntity("route", "services", "ai", new JsonObject { [field] = JsonNode.Parse(value) }));

    [TestCase("route", "regions", "us")]
    [TestCase("route", "services", "ai")]
    [TestCase("dns", "resolvers", "alidns")]
    public void RejectsDuplicateEntityIds(string section, string field, string id) => Assert.Throws<ProfileValidationException>(() =>
        BoxForgeProfile.Default.ApplyOverrides(ArrayOperation(section, field, "append", Entity(section, field, id))));

    [Test]
    public void RejectsDuplicateEntityPropertyNames() => Assert.Throws<ProfileValidationException>(() => Load("""
        {"route":{"regions":{"append":[{"id":"tw","id":"tw","display_name":"TW","pattern":"taiwan"}]}}}
        """));

    [TestCase(TargetPlatform.Android, false)]
    [TestCase(TargetPlatform.Android, true)]
    [TestCase(TargetPlatform.Linux, false)]
    [TestCase(TargetPlatform.Linux, true)]
    [TestCase(TargetPlatform.Windows, false)]
    [TestCase(TargetPlatform.Windows, true)]
    public async Task CompleteServiceReplacementUpdatesGroupsDnsAndFixedRouteStages(TargetPlatform platform, bool priority)
    {
        var profile = BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "regions", "append",
            JsonNode.Parse("""{"id":"tw","display_name":"TW","pattern":"taiwan"}""")!));
        profile = ReplaceEntity("route", "services", "ai", new JsonObject
        {
            ["name"] = "Custom AI",
            ["default_region"] = "tw",
            ["precedes_domestic_routes"] = priority,
            ["rule_sets"] = new JsonArray("games")
        }, profile);
        using var document = await Generate(profile, platform, Yaml("taiwan01", "taiwan02"));
        JsonElement root = document.RootElement;
        var group = root.GetProperty("outbounds").EnumerateArray().Single(item => item.GetProperty("tag").GetString() == "Custom AI");
        Assert.That(group.GetProperty("default").GetString(), Is.EqualTo("TW"));
        var rules = root.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
        int customIndex = Array.FindIndex(rules, rule => rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == "Custom AI");
        int domesticIndex = Array.FindIndex(rules, rule => rule.TryGetProperty("rule_set", out var sets)
            && sets.EnumerateArray().Any(tag => tag.GetString() == "cn") && rule.TryGetProperty("network", out var network)
            && network.EnumerateArray().Any(item => item.GetString() == "udp"));
        if (priority) Assert.That(customIndex, Is.LessThan(domesticIndex));
        else Assert.That(customIndex, Is.GreaterThan(domesticIndex));
        Assert.That(rules[customIndex].GetProperty("rule_set").EnumerateArray().Select(tag => tag.GetString()), Is.EqualTo(new[] { "games" }));
        Assert.That(root.GetProperty("dns").GetProperty("rules").EnumerateArray().Any(rule =>
            rule.TryGetProperty("rule_set", out var sets) && sets.EnumerateArray().Any(tag => tag.GetString() == "games")), Is.True);
    }

    [Test]
    public async Task CompleteRuleSetReplacementPreservesGroupingOtherSourcesAndTemplateOccurrences()
    {
        const string url = "https://rules.example.net/{tag}/{tag}.srs?set={tag}";
        var profile = ReplaceRuleSet(new JsonObject { ["url"] = url, ["update_interval"] = "2h" });
        var originalSources = Resolved(BoxForgeProfile.Default)["route"]!["rule_sets"]!.AsArray();
        var sources = Resolved(profile)["route"]!["rule_sets"]!.AsArray();
        Assert.That(JsonNode.DeepEquals(sources[0]!["tags"], originalSources[0]!["tags"]), Is.True);
        Assert.That(sources.Count, Is.EqualTo(originalSources.Count));
        for (int index = 1; index < sources.Count; index++)
            Assert.That(JsonNode.DeepEquals(sources[index], originalSources[index]), Is.True);
        using var document = await Generate(profile, TargetPlatform.Linux, Yaml("US01"));
        JsonElement generated = document.RootElement.GetProperty("route").GetProperty("rule_set")[0];
        Assert.That(generated.GetProperty("url").GetString(), Is.EqualTo(url));
        Assert.That(generated.GetProperty("update_interval").GetString(), Is.EqualTo("2h"));
    }

    [TestCase("1ns", "1ns")]
    [TestCase("1d", "1d")]
    [TestCase(".5d", "43200000000000ns")]
    [TestCase("1d12h", "1d12h")]
    [TestCase("+2h", "+2h")]
    [TestCase("9223372036854775807ns", "9223372036854775807ns")]
    [TestCase("106751.991167300645925925925924d", "9223372036854775807ns")]
    public async Task RuleSetIntervalsAcceptPositiveExtendedDurations(string interval, string expected)
    {
        var profile = ReplaceRuleSet(new JsonObject { ["update_interval"] = interval });
        JsonNode source = Resolved(profile)["route"]!["rule_sets"]!.AsArray()[0]!;
        Assert.That(source["update_interval"]!.GetValue<string>(), Is.EqualTo(interval));
        using var document = await Generate(profile, TargetPlatform.Linux, Yaml("US01"));
        Assert.That(document.RootElement.GetProperty("route").GetProperty("rule_set")[0]
            .GetProperty("update_interval").GetString(), Is.EqualTo(expected));
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
    public void RuleSetIntervalsRejectInvalidOrOutOfRangeValues(string interval) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceRuleSet(new JsonObject { ["update_interval"] = interval }));

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
    public void RuleSetUrlsRejectInvalidOrUnsupportedUrls(string url) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceRuleSet(new JsonObject { ["url"] = url }));

    [TestCase("tags", "null")]
    [TestCase("tags", "[]")]
    [TestCase("tags", "[null]")]
    [TestCase("tags", "[1]")]
    [TestCase("url", "null")]
    [TestCase("update_interval", "null")]
    [TestCase("update_interval", "12")]
    [TestCase("format", "\"source\"")]
    [TestCase("http_client", "\"direct\"")]
    [TestCase("path", "\"file.srs\"")]
    [TestCase("tag", "\"ai\"")]
    public void RuleSetOperationsRejectMalformedCompleteEntities(string field, string value) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceRuleSet(new JsonObject { [field] = JsonNode.Parse(value) }));

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
    [TestCase("🇺🇸 US")]
    [TestCase("🤖 AI")]
    public void RuleSetTagsRejectInvalidNamesAndEffectiveTagCollisions(string tag) => Assert.Throws<ProfileValidationException>(() =>
        BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "rule_sets", "append", new JsonObject
        { ["tags"] = new JsonArray(tag), ["url"] = "https://rules.example.net/new.srs", ["update_interval"] = "1d" })));

    [Test]
    public void RuleSetSourcesRejectDuplicateTagsAcrossAndWithinEntities()
    {
        var existing = Resolved(BoxForgeProfile.Default)["route"]!["rule_sets"]!.AsArray()[0]!.DeepClone();
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "rule_sets", "append", existing)));
        Assert.Throws<ProfileValidationException>(() => ReplaceRuleSet(new JsonObject { ["tags"] = new JsonArray("ai", "ai") }));
    }

    [Test]
    public void MultipleRuleSetTagsRequireAUrlTemplate()
    {
        var source = new JsonObject
        {
            ["tags"] = new JsonArray("a", "b"),
            ["url"] = "https://rules.example.net/shared.srs",
            ["update_interval"] = "1d"
        };
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.ApplyOverrides(
            ArrayOperation("route", "rule_sets", "append", source)));
    }

    [Test]
    public async Task SingletonRuleSetTemplatesExpandEveryOccurrenceBeforeEmission()
    {
        var source = new JsonObject
        {
            ["tags"] = new JsonArray("custom"),
            ["url"] = "https://rules.example.net/{tag}/{tag}.srs?set={tag}",
            ["update_interval"] = "1d"
        };
        var profile = BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "rule_sets", "append", source));
        using var document = await Generate(profile, TargetPlatform.Linux, Yaml("US01"));
        var generated = document.RootElement.GetProperty("route").GetProperty("rule_set").EnumerateArray()
            .Single(item => item.GetProperty("tag").ValueKind == JsonValueKind.String
                && item.GetProperty("tag").GetString() == "custom");
        Assert.That(generated.GetProperty("url").GetString(), Is.EqualTo("https://rules.example.net/custom/custom.srs?set=custom"));
    }

    [TestCase(TargetPlatform.Android, false)]
    [TestCase(TargetPlatform.Android, true)]
    [TestCase(TargetPlatform.Linux, false)]
    [TestCase(TargetPlatform.Linux, true)]
    [TestCase(TargetPlatform.Windows, false)]
    [TestCase(TargetPlatform.Windows, true)]
    public async Task AppendedRuleSetAndServiceGenerateCompleteReferences(TargetPlatform platform, bool priority)
    {
        var overlay = new JsonObject
        {
            ["route"] = new JsonObject
            {
                ["rule_sets"] = new JsonObject
                {
                    ["append"] = new JsonArray(new JsonObject
                    { ["tags"] = new JsonArray("netflix"), ["url"] = "https://rules.example.net/{tag}.srs", ["update_interval"] = "2h" })
                },
                ["services"] = new JsonObject
                {
                    ["append"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "netflix",
                        ["name"] = "Netflix",
                        ["default_region"] = null,
                        ["rule_sets"] = new JsonArray("netflix"),
                        ["precedes_domestic_routes"] = priority
                    })
                }
            }
        };
        using var document = await Generate(BoxForgeProfile.Default.ApplyOverrides(overlay), platform, Yaml("US01", "US02"));
        JsonElement root = document.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("outbounds").EnumerateArray().Any(item => item.GetProperty("tag").GetString() == "Netflix"), Is.True);
            Assert.That(root.GetProperty("route").GetProperty("rule_set").EnumerateArray().Any(item =>
                HasTag(item.GetProperty("tag"), "netflix")
                && item.GetProperty("url").GetString() == "https://rules.example.net/netflix.srs"), Is.True);
            Assert.That(root.GetProperty("route").GetProperty("rules").EnumerateArray().Any(rule =>
                rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == "Netflix"
                && rule.TryGetProperty("rule_set", out var tags) && HasTag(tags, "netflix")), Is.True);
            Assert.That(root.GetProperty("dns").GetProperty("rules").EnumerateArray().Any(rule =>
                rule.TryGetProperty("rule_set", out var tags) && HasTag(tags, "netflix")), Is.True);
        });

        static bool HasTag(JsonElement value, string expected) => value.ValueKind == JsonValueKind.String
            ? value.GetString() == expected
            : value.EnumerateArray().Any(tag => tag.GetString() == expected);
    }

    private static IEnumerable<TestCaseData> ResolverCases()
    {
        foreach (var (id, tag) in new[] { ("alidns", SingboxTags.DirectAliDns), ("tencent", SingboxTags.DirectTencentDns),
                     ("cloudflare", SingboxTags.ProxyCloudflareDns), ("google", SingboxTags.ProxyGoogleDns) })
            foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
                yield return new TestCaseData(id, tag, platform);
    }

    [TestCaseSource(nameof(ResolverCases))]
    public async Task CompleteResolverReplacementChangesDohTlsAndBootstrapRouteTogether(string id, string tag, TargetPlatform platform)
    {
        var profile = ReplaceEntity("dns", "resolvers", id, new JsonObject { ["server"] = "192.0.2.53", ["server_name"] = "dns.example.net" });
        using var document = await Generate(profile, platform, Yaml("US01"));
        JsonElement root = document.RootElement;
        var affectedTags = new HashSet<string>(StringComparer.Ordinal) { tag };
        if (id is "cloudflare" or "google")
            affectedTags.UnionWith(BoxForgeProfile.Default.Services.Select(service => SingboxTags.ServiceDns(service.Id, id)));
        JsonElement[] servers = root.GetProperty("dns").GetProperty("servers").EnumerateArray()
            .Where(item => item.TryGetProperty("server", out _)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(servers.Count(server => affectedTags.Contains(server.GetProperty("tag").GetString()!)),
                Is.EqualTo(affectedTags.Count));
            foreach (JsonElement server in servers)
            {
                bool affected = affectedTags.Contains(server.GetProperty("tag").GetString()!);
                Assert.That(server.GetProperty("server").GetString(), affected
                    ? Is.EqualTo("192.0.2.53") : Is.Not.EqualTo("192.0.2.53"));
                Assert.That(server.GetProperty("tls").GetProperty("server_name").GetString(), affected
                    ? Is.EqualTo("dns.example.net") : Is.Not.EqualTo("dns.example.net"));
            }
            if (id == "alidns")
                Assert.That(root.GetProperty("route").GetProperty("rules").EnumerateArray().Any(rule =>
                    rule.TryGetProperty("ip_cidr", out var cidrs) && cidrs.EnumerateArray().Any(cidr => cidr.GetString() == "192.0.2.53/32")), Is.True);
        });
    }

    [TestCase("id", "null")]
    [TestCase("id", "123")]
    [TestCase("id", "\"ALIDNS\"")]
    [TestCase("id", "\"quad9\"")]
    [TestCase("server", "null")]
    [TestCase("server", "123")]
    [TestCase("server_name", "null")]
    [TestCase("detour", "\"PROXY\"")]
    [TestCase("insecure", "true")]
    [TestCase("type", "\"udp\"")]
    [TestCase("port", "53")]
    [TestCase("path", "\"/resolve\"")]
    [TestCase("primary", "\"google\"")]
    public void ResolverOperationsRejectMalformedCompleteEntities(string field, string value) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceEntity("dns", "resolvers", "alidns", new JsonObject { [field] = JsonNode.Parse(value) }));

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
    public void ResolverServersRequireCanonicalUnicastIpv4(string server) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceEntity("dns", "resolvers", "alidns", new JsonObject { ["server"] = server }));

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
    public void ResolverTlsNamesRequireAsciiDnsNames(string name) => Assert.Throws<ProfileValidationException>(() =>
        ReplaceEntity("dns", "resolvers", "alidns", new JsonObject { ["server_name"] = name }));

    [Test]
    public void ResolverTlsNamesRejectOverlongLabelsAndHostnames()
    {
        foreach (string name in new[] { new string('a', 64) + ".example.net", string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) })
            Assert.Throws<ProfileValidationException>(() => ReplaceEntity("dns", "resolvers", "alidns", new JsonObject { ["server_name"] = name }));
    }

    [TestCase("10.0.0.53", 63)]
    [TestCase("127.0.0.1", 253)]
    public void ResolverValidationAllowsLanLoopbackAndMaximumDnsNameLengths(string server, int length)
    {
        string name = length == 63 ? new string('a', 63) : string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        var profile = ReplaceEntity("dns", "resolvers", "alidns", new JsonObject { ["server"] = server, ["server_name"] = name });
        JsonNode resolver = Resolved(profile)["dns"]!["resolvers"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "alidns")!;
        Assert.That(resolver["server_name"]!.GetValue<string>(), Has.Length.EqualTo(length));
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task PlatformSectionsControlTheCompleteGenerationPath(TargetPlatform platform)
    {
        var profile = Load("""
            {"dns":{"address_family":"DualStack"},
             "platforms":{"Android":{"dns":{"selection_mode":"ParallelFastest"}},
                          "Linux":{"dns":{"selection_mode":"SequentialFallback"}},
                          "Windows":{"dns":{"selection_mode":"SequentialFallback"}}}}
            """);
        var patch = JsonNode.Parse("""
            {"route":{"strict_route":false,"auto_redirect":false,"multi_queue":false,"use_direct_forwarding":false},
             "features":{"tailscale":{"taildrop_directory":"CustomTaildrop"}},
             "tuning":{"tcp_keep_alive":"7m","tcp_keep_alive_interval":"17s","url_test_interval":"7m"}}
            """)!;
        var overlay = new JsonObject { ["platforms"] = new JsonObject { [platform.ToString()] = patch } };
        profile = profile.ApplyOverrides(overlay);
        using var document = await Generate(profile, platform, Yaml("US01", "US02"));
        JsonElement root = document.RootElement;
        JsonElement tun = root.GetProperty("inbounds")[0];
        var outbounds = root.GetProperty("outbounds").EnumerateArray().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(tun.GetProperty("strict_route").GetBoolean(), Is.False);
            Assert.That(tun.GetProperty("auto_redirect").GetBoolean(), Is.False);
            Assert.That(tun.GetProperty("multi_queue").GetBoolean(), Is.False);
            Assert.That(outbounds.Any(item => item.GetProperty("type").GetString() == "bridge"), Is.False);
            foreach (var proxy in outbounds.Where(item => item.GetProperty("type").GetString() == "shadowsocks"))
            {
                Assert.That(proxy.GetProperty("tcp_keep_alive").GetString(), Is.EqualTo("7m"));
                Assert.That(proxy.GetProperty("tcp_keep_alive_interval").GetString(), Is.EqualTo("17s"));
            }
            foreach (var auto in outbounds.Where(item => item.GetProperty("type").GetString() == "urltest"))
                Assert.That(auto.GetProperty("interval").GetString(), Is.EqualTo("7m"));
            Assert.That(root.GetProperty("dns").GetProperty("rules").EnumerateArray().Any(rule =>
                rule.TryGetProperty("race", out var race) && race.GetBoolean()), Is.EqualTo(platform == TargetPlatform.Android));
            Assert.That(root.GetProperty("dns").GetProperty("rules").EnumerateArray().Any(rule =>
                rule.TryGetProperty("query_type", out var types) && types.EnumerateArray().Any(type => type.GetString() == "AAAA")), Is.False);
            Assert.That(root.GetProperty("route").GetProperty("rules").EnumerateArray().Any(rule => rule.TryGetProperty("ip_version", out _)), Is.False);
            foreach (var client in root.GetProperty("http_clients").EnumerateArray())
                Assert.That(client.GetProperty("domain_resolver").TryGetProperty("strategy", out _), Is.False);
            Assert.That(root.GetProperty("endpoints")[0].GetProperty("taildrop_directory").GetString(), Is.EqualTo("CustomTaildrop"));
        });
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task PlatformFeatureKeepsTailscaleEndpointDnsAndRouteTogether(TargetPlatform platform)
    {
        foreach (bool enabled in new[] { false, true })
        {
            var profile = BoxForgeProfile.Default.ApplyOverrides(new JsonObject
            {
                ["features"] = new JsonObject { ["tailscale"] = new JsonObject { ["enabled"] = !enabled } },
                ["platforms"] = new JsonObject
                {
                    [platform.ToString()] = new JsonObject
                    { ["features"] = new JsonObject { ["tailscale"] = new JsonObject { ["enabled"] = enabled } } }
                }
            });
            using var document = await Generate(profile, platform, Yaml("US01"));
            JsonElement root = document.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(root.TryGetProperty("endpoints", out _), Is.EqualTo(enabled));
                Assert.That(root.GetProperty("dns").GetProperty("servers").EnumerateArray()
                    .Any(server => server.GetProperty("tag").GetString() == SingboxTags.TailscaleDns), Is.EqualTo(enabled));
                Assert.That(root.GetProperty("route").GetProperty("rules").EnumerateArray().Any(rule =>
                    rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == SingboxTags.TailscaleEndpoint), Is.EqualTo(enabled));
            });
        }
    }

    [Test]
    public async Task ProvidersKeepIndependentProfilesAndReserveTheirEffectiveNames()
    {
        var customized = ReplaceEntity("route", "regions", "us", new JsonObject { ["display_name"] = "Provider US" });
        string path = Path.Combine(directory, "provider.json");
        File.WriteAllText(path, new JsonObject
        {
            ["route"] = new JsonObject
            { ["regions"] = new JsonObject { ["replace"] = Resolved(customized)["route"]!["regions"]!.DeepClone() } }
        }.ToJsonString());
        using var custom = CreateProvider(path);
        using var ordinary = CreateProvider(null);
        var customEngine = custom.GetRequiredService<IBoxForgeEngine>();
        var ordinaryEngine = ordinary.GetRequiredService<IBoxForgeEngine>();
        Assert.ThrowsAsync<BoxForgeConversionException>(() => customEngine.ConvertAsync(
            new ConversionRequest("reserved", Yaml("Provider US"), [TargetPlatform.Linux])));
        Assert.DoesNotThrowAsync(() => ordinaryEngine.ConvertAsync(
            new ConversionRequest("ordinary", Yaml("Provider US"), [TargetPlatform.Linux])));
        var request = new ConversionRequest("same", Yaml("US01", "US02"), [TargetPlatform.Linux]);
        var customBundle = await customEngine.ConvertAsync(request);
        var ordinaryBundle = await ordinaryEngine.ConvertAsync(request);
        Assert.That(customBundle.Artifacts.Single().Content, Does.Contain("Provider US"));
        Assert.That(ordinaryBundle.Artifacts.Single().Content, Does.Not.Contain("Provider US"));
    }

    [Test]
    public async Task RequestProfileControlsReservedNamesAndEmissionWithoutChangingOtherRequests()
    {
        var host = BoxForgeProfile.Default;
        var changed = ReplaceEntity("route", "regions", "us", new JsonObject { ["display_name"] = "Request US" });
        var overlay = new JsonObject
        {
            ["platforms"] = new JsonObject
            {
                ["Linux"] = new JsonObject
                {
                    ["route"] = new JsonObject
                    { ["regions"] = new JsonObject { ["replace"] = Resolved(changed)["route"]!["regions"]!.DeepClone() } }
                }
            }
        };
        using var provider = CreateProvider(null);
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        Assert.ThrowsAsync<BoxForgeConversionException>(() => engine.ConvertAsync(
            new ConversionRequest("reserved", Yaml("Request US"), [TargetPlatform.Linux]) { Profile = overlay }));
        var request = new ConversionRequest("valid", Yaml("US01", "US02"), [TargetPlatform.Linux]);
        using var custom = JsonDocument.Parse((await engine.ConvertAsync(request with { Profile = overlay })).Artifacts.Single().Content);
        using var ordinary = JsonDocument.Parse((await engine.ConvertAsync(request)).Artifacts.Single().Content);
        Assert.That(custom.RootElement.GetProperty("outbounds").EnumerateArray().Any(item => item.GetProperty("tag").GetString() == "Request US"), Is.True);
        Assert.That(ordinary.RootElement.GetProperty("outbounds").EnumerateArray().Any(item => item.GetProperty("tag").GetString() == "Request US"), Is.False);
        Assert.DoesNotThrowAsync(() => engine.ConvertAsync(new ConversionRequest("host", Yaml("Request US"), [TargetPlatform.Linux])));
        Assert.DoesNotThrowAsync(() => engine.ConvertAsync(new ConversionRequest("other-platform", Yaml("Request US"), [TargetPlatform.Windows])
        { Profile = overlay }));
        Assert.That(host.Resolve(TargetPlatform.Linux).Regions.Single(region => region.Id == "us").DisplayName, Is.Not.EqualTo("Request US"));
    }

    [Test]
    public void RegexTimeoutFailsConversionInsteadOfReturningPartialOutput()
    {
        var profile = ReplaceEntity("route", "regions", "us", new JsonObject { ["pattern"] = "(a+)+$" });
        using var provider = new ServiceCollection().AddLogging().AddBoxForgeCore(new ConfigurationBuilder().Build())
            .AddSingleton(profile).BuildServiceProvider();
        Assert.ThrowsAsync<BoxForgePlatformConversionException>(() => provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(
            new ConversionRequest("timeout", Yaml(new string('a', 20_000) + "!"), [TargetPlatform.Linux])));
    }

    [Test]
    public void ResolvedJsonIncludesCanonicalSectionsAndCompletePlatformSnapshots()
    {
        var profile = Load("""{"features":{"api":{"enabled":true}},"tuning":{"connect_timeout":"9s"}}""");
        var root = Resolved(profile);
        Assert.That(root.Select(property => property.Key), Is.EquivalentTo(new[] { "features", "dns", "route", "tuning", "privacy", "platforms" }));
        Assert.That(root.ContainsKey("version"), Is.False);
        foreach (var platform in Enum.GetValues<TargetPlatform>())
        {
            var resolved = root["platforms"]![platform.ToString()]!.AsObject();
            Assert.That(resolved.Select(property => property.Key), Is.EquivalentTo(new[] { "features", "dns", "route", "tuning", "privacy" }));
            Assert.That(resolved["features"]!["api"]!["enabled"]!.GetValue<bool>(), Is.True);
        }
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public async Task KeyedServicePatchPreservesOrderAndChangesOnlyItsSelectorDefault(TargetPlatform platform)
    {
        var original = BoxForgeProfile.Default;
        var overlay = JsonNode.Parse("""
            {"route":{"services":{"patch":[{"id":"ai","default_region":"jp"}]}}}
            """)!.AsObject();
        var changed = original.ApplyOverrides(overlay);
        Assert.That(changed.Services.Select(service => service.Id), Is.EqualTo(original.Services.Select(service => service.Id)));
        var expectedServices = (JsonArray)Resolved(original)["route"]!["services"]!.DeepClone();
        expectedServices.OfType<JsonObject>().Single(service => service["id"]!.GetValue<string>() == "ai")["default_region"] = "jp";
        Assert.That(JsonNode.DeepEquals(Resolved(changed)["route"]!["services"], expectedServices), Is.True);

        string yaml = Yaml("US01", "US02", "JP01", "JP02");
        using var baseline = await Generate(original, platform, yaml);
        using var actual = await Generate(changed, platform, yaml);
        var expected = JsonNode.Parse(baseline.RootElement.GetRawText())!.AsObject();
        string aiName = original.Services.Single(service => service.Id == "ai").Name;
        expected["outbounds"]!.AsArray().OfType<JsonObject>().Single(outbound => outbound["tag"]!.GetValue<string>() == aiName)["default"] =
            changed.Resolve(TargetPlatform.Linux).Regions.Single(region => region.Id == "jp").DisplayName;
        Assert.Multiple(() =>
        {
            Assert.That(actual.RootElement.GetProperty("route").GetProperty("rules").GetRawText(),
                Is.EqualTo(baseline.RootElement.GetProperty("route").GetProperty("rules").GetRawText()),
                "Patching a service must not reorder outbound route decisions.");
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual.RootElement.GetRawText()), expected), Is.True,
                "Only the selected service's default target may change.");
        });
    }

    [Test]
    public async Task KeyedRegionResolverAndGroupedRuleSetPatchesPreserveOtherFieldsAndOrder()
    {
        var original = BoxForgeProfile.Default;
        var profile = Load("""
            {"route":{"regions":{"patch":[{"id":"jp","display_name":"Japan custom"}]},
                      "rule_sets":{"patch":[{"tag":"games","url":"https://rules.example.net/{tag}.srs"}]}},
             "dns":{"resolvers":{"patch":[{"id":"alidns","server":"223.6.6.6"}]}}}
            """);
        var before = Resolved(original);
        var after = Resolved(profile);
        Assert.Multiple(() =>
        {
            Assert.That(profile.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Id), Is.EqualTo(original.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Id)));
            Assert.That(profile.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Pattern.ToString()),
                Is.EqualTo(original.Resolve(TargetPlatform.Linux).Regions.Select(region => region.Pattern.ToString())));
            Assert.That(after["dns"]!["resolvers"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()),
                Is.EqualTo(before["dns"]!["resolvers"]!.AsArray().Select(item => item!["id"]!.GetValue<string>())));
            Assert.That(after["dns"]!["resolvers"]![0]!["server_name"]!.GetValue<string>(),
                Is.EqualTo(before["dns"]!["resolvers"]![0]!["server_name"]!.GetValue<string>()));
            Assert.That(JsonNode.DeepEquals(after["route"]!["rule_sets"]![0]!["tags"], before["route"]!["rule_sets"]![0]!["tags"]), Is.True);
            Assert.That(after["route"]!["rule_sets"]![0]!.AsObject().ContainsKey("tag"), Is.False);
            Assert.That(after["route"]!["rule_sets"]![0]!["url"]!.GetValue<string>(), Is.EqualTo("https://rules.example.net/{tag}.srs"));
        });
        using var generated = await Generate(profile, TargetPlatform.Linux, Yaml("US01", "US02", "JP01", "JP02"));
        Assert.Multiple(() =>
        {
            Assert.That(generated.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray()
                .Single(server => server.GetProperty("tag").GetString() == SingboxTags.DirectAliDns).GetProperty("server").GetString(),
                Is.EqualTo("223.6.6.6"));
            Assert.That(generated.RootElement.GetProperty("outbounds").EnumerateArray()
                .Where(outbound => outbound.GetProperty("type").GetString() == "urltest").Select(outbound => outbound.GetProperty("tag").GetString()),
                Is.EqualTo(new[] { original.Resolve(TargetPlatform.Linux).Regions.Single(region => region.Id == "us").DisplayName + " AUTO", "Japan custom AUTO" }));
            Assert.That(generated.RootElement.GetProperty("route").GetProperty("rule_set").EnumerateArray().First().GetProperty("url").GetString(),
                Is.EqualTo("https://rules.example.net/{tag}.srs"));
        });
    }

    [Test]
    public void KeyedOperationsUseFixedOrderAndSequentialItemsWithoutMovingReplacements()
    {
        var ai = Entity("route", "services", "ai");
        var google = Entity("route", "services", "google");
        var custom = (JsonObject)ai.DeepClone();
        custom["id"] = "custom";
        custom["name"] = "Custom service";
        var aiReplacement = (JsonObject)ai.DeepClone();
        aiReplacement["default_region"] = "hk";
        var inserted = (JsonObject)custom.DeepClone();
        inserted["id"] = "inserted";
        inserted["name"] = "Inserted service";
        var secondInsert = (JsonObject)inserted.DeepClone();
        secondInsert["id"] = "second-insert";
        secondInsert["name"] = "Second inserted service";
        var operations = new JsonObject
        {
            ["insert_before"] = new JsonArray(
                new JsonObject { ["before"] = new JsonObject { ["id"] = "google" }, ["value"] = inserted },
                new JsonObject { ["before"] = new JsonObject { ["id"] = "google" }, ["value"] = secondInsert }),
            ["remove_by_key"] = new JsonArray(new JsonObject { ["id"] = "custom" }),
            ["set"] = new JsonArray(aiReplacement),
            ["patch"] = new JsonArray(new JsonObject { ["id"] = "ai", ["default_region"] = "jp" },
                new JsonObject { ["id"] = "custom", ["default_region"] = "jp" }),
            ["remove"] = new JsonArray(google.DeepClone()),
            ["append"] = new JsonArray(google.DeepClone(), custom),
            ["replace"] = new JsonArray(ai.DeepClone(), google.DeepClone())
        };
        var profile = BoxForgeProfile.Default.ApplyOverrides(new JsonObject
        { ["route"] = new JsonObject { ["services"] = operations } });
        Assert.Multiple(() =>
        {
            Assert.That(profile.Services.Select(service => service.Id), Is.EqualTo(new[] { "ai", "inserted", "second-insert", "google" }));
            Assert.That(profile.Services[0].DefaultRegion, Is.EqualTo("hk"), "set must execute after patch without moving ai.");
        });
        var sequential = profile.ApplyOverrides(JsonNode.Parse("""
            {"route":{"services":{"patch":[{"id":"ai","default_region":"jp"},{"id":"ai","default_region":"sg"}]}}}
            """)!.AsObject());
        Assert.That(sequential.Services[0].DefaultRegion, Is.EqualTo("sg"));
    }

    [Test]
    public void KeyedPatchRequiresAndAppliesNestedArrayOperations()
    {
        var profile = Load("""
            {"route":{"services":{"patch":[{"id":"ai","rule_sets":{"append":["google"]}}]}}}
            """);
        Assert.That(profile.Services.Single(service => service.Id == "ai").RuleSets, Is.EqualTo(new[] { "ai", "google" }));
    }

    [TestCase("set")]
    [TestCase("insert_before")]
    public void KeyedReplacementAndInsertionRequireAnExistingTarget(string operation)
    {
        var value = Entity("route", "services", "ai");
        value["id"] = "custom-new";
        value["name"] = "Complete new service";
        JsonObject item = operation == "set" ? value : new JsonObject
        {
            ["before"] = new JsonObject { ["id"] = "missing" },
            ["value"] = value
        };
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "services", operation, item)));
    }

    [Test]
    public void GroupedRuleSetSetPreservesItsPositionAndRequiresTheSelectedTag()
    {
        var source = JsonNode.Parse("""
            {"tags":["custom-first","custom-second"],"url":"https://rules.example.net/{tag}.srs","update_interval":"1d"}
            """)!.AsObject();
        var original = BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "rule_sets", "append", source));
        int index = Resolved(original)["route"]!["rule_sets"]!.AsArray().Count - 1;
        source["tag"] = "custom-second";
        source["url"] = "https://replacement.example.net/{tag}.srs";
        var profile = original.ApplyOverrides(ArrayOperation("route", "rule_sets", "set", source));
        Assert.Multiple(() =>
        {
            Assert.That(JsonNode.DeepEquals(Resolved(profile)["route"]!["rule_sets"]![index]!["tags"], source["tags"]), Is.True);
            Assert.That(Resolved(profile)["route"]!["rule_sets"]![index]!.AsObject().ContainsKey("tag"), Is.False);
            Assert.That(Resolved(profile)["route"]!["rule_sets"]![index]!["url"]!.GetValue<string>(),
                Is.EqualTo("https://replacement.example.net/{tag}.srs"));
        });
        source["tags"] = new JsonArray("custom-first");
        Assert.Throws<ProfileValidationException>(() => original.ApplyOverrides(ArrayOperation("route", "rule_sets", "set", source)));
        Assert.Throws<ProfileValidationException>(() => original.ApplyOverrides(ArrayOperation("route", "rule_sets", "patch", JsonNode.Parse("""
            {"tag":"custom-second","tags":{"remove":["custom-second"]}}
            """)!)));
    }

    [Test]
    public void RuleSetRemovalByMemberTagRemovesTheWholeSource()
    {
        var source = JsonNode.Parse("""
            {"tags":["custom-first","custom-second"],"url":"https://rules.example.net/{tag}.srs","update_interval":"1d"}
            """)!;
        var added = BoxForgeProfile.Default.ApplyOverrides(ArrayOperation("route", "rule_sets", "append", source));
        var removed = added.ApplyOverrides(ArrayOperation("route", "rule_sets", "remove_by_key",
            new JsonObject { ["tag"] = "custom-second" }));
        Assert.That(JsonNode.DeepEquals(Resolved(removed), Resolved(BoxForgeProfile.Default)), Is.True);
    }

    [Test]
    public void KeyedPatchLayersAreSnapshotsAndFailAtomically()
    {
        var root = JsonNode.Parse("""
            {"route":{"services":{"patch":[{"id":"ai","default_region":"jp"}]}},
             "platforms":{"Linux":{"route":{"services":{"patch":[{"id":"ai","default_region":"hk"}]}}}}}
            """)!.AsObject();
        string original = BoxForgeProfile.Default.ToResolvedJson();
        string rootInput = root.ToJsonString();
        var profile = BoxForgeProfile.Default.ApplyOverrides(root);
        var request = JsonNode.Parse("""
            {"route":{"services":{"patch":[{"id":"ai","default_region":"sg"}]}},
             "platforms":{"Linux":{"route":{"services":{"patch":[{"id":"ai","default_region":"us"}]}}}}}
            """)!.AsObject();
        string requestInput = request.ToJsonString();
        var selected = profile.Resolve(TargetPlatform.Linux, request);
        string snapshot = selected.ToResolvedJson();
        Assert.Multiple(() =>
        {
            Assert.That(profile.Resolve(TargetPlatform.Windows).Services.Single(service => service.Id == "ai").DefaultRegion, Is.EqualTo("jp"));
            Assert.That(profile.Resolve(TargetPlatform.Linux).Services.Single(service => service.Id == "ai").DefaultRegion, Is.EqualTo("hk"));
            Assert.That(profile.Resolve(TargetPlatform.Windows, request).Services.Single(service => service.Id == "ai").DefaultRegion, Is.EqualTo("sg"));
            Assert.That(selected.Services.Single(service => service.Id == "ai").DefaultRegion, Is.EqualTo("us"));
            Assert.That(root.ToJsonString(), Is.EqualTo(rootInput));
            Assert.That(request.ToJsonString(), Is.EqualTo(requestInput));
        });
        root["route"]!["services"]!["patch"]![0]!["default_region"] = "sg";
        request["route"]!["services"]!["patch"]![0]!["default_region"] = "jp";
        Assert.That(selected.ToResolvedJson(), Is.EqualTo(snapshot));
        string hostSnapshot = profile.ToResolvedJson();
        var invalid = JsonNode.Parse("""
            {"route":{"services":{"patch":[{"id":"ai","default_region":"jp"},{"id":"missing","default_region":"hk"}]}}}
            """)!.AsObject();
        string invalidInput = invalid.ToJsonString();
        Assert.Throws<ProfileValidationException>(() => profile.ApplyOverrides(invalid));
        Assert.That(profile.ToResolvedJson(), Is.EqualTo(hostSnapshot));
        Assert.That(invalid.ToJsonString(), Is.EqualTo(invalidInput));
        Assert.That(BoxForgeProfile.Default.ToResolvedJson(), Is.EqualTo(original));
    }

    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("[null]")]
    [TestCase("[{}]")]
    [TestCase("[{\"id\":null}]")]
    [TestCase("[{\"id\":12}]")]
    [TestCase("[{\"tag\":\"ai\",\"default_region\":\"jp\"}]")]
    [TestCase("[{\"id\":\"ai\",\"typo\":true}]")]
    [TestCase("[{\"id\":\"missing\",\"default_region\":\"jp\"}]")]
    [TestCase("[{\"id\":\"ai\",\"rule_sets\":[\"ai\"]}]")]
    public void KeyedPatchRejectsMalformedSelectorsFieldsAndNestedArrays(string entries) => Assert.Throws<ProfileValidationException>(() =>
        Load("{\"route\":{\"services\":{\"patch\":" + entries + "}}}"));

    [TestCase("set", "[{\"id\":\"ai\",\"name\":\"Incomplete\"}]")]
    [TestCase("set", "[{\"id\":\"missing\",\"name\":\"Incomplete\"}]")]
    [TestCase("remove_by_key", "[{\"id\":\"ai\",\"name\":\"extra-selector-field\"}]")]
    [TestCase("remove_by_key", "[{\"id\":\"missing\"}]")]
    [TestCase("insert_before", "[{\"before\":{\"id\":\"missing\"},\"value\":{\"id\":\"new\"}}]")]
    [TestCase("insert_before", "[{\"before\":{\"id\":\"google\"}}]")]
    [TestCase("insert_before", "[{\"before\":{\"id\":\"google\"},\"value\":null}]")]
    [TestCase("insert_before", "[{\"before\":{\"id\":\"google\"},\"value\":{\"id\":\"new\"},\"typo\":true}]")]
    public void KeyedSetRemoveAndInsertRejectInvalidShapes(string operation, string entries) => Assert.Throws<ProfileValidationException>(() =>
        Load("{\"route\":{\"services\":{\"" + operation + "\":" + entries + "}}}"));

    [TestCase("patch")]
    [TestCase("set")]
    [TestCase("remove_by_key")]
    [TestCase("insert_before")]
    public void PrimitiveArraysRejectKeyedOperationsEvenWhenEmpty(string operation) => Assert.Throws<ProfileValidationException>(() =>
        Load("{\"route\":{\"sniff_excluded_ports\":{\"" + operation + "\":[]}}}"));

    [Test]
    public void AmbiguousKeyedRemovalRejectsInsteadOfRemovingEveryMatch()
    {
        var duplicate = Entity("route", "services", "ai");
        var overlay = new JsonObject
        {
            ["route"] = new JsonObject
            {
                ["services"] = new JsonObject
                {
                    ["append"] = new JsonArray(duplicate),
                    ["remove_by_key"] = new JsonArray(new JsonObject { ["id"] = "ai" })
                }
            }
        };
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.ApplyOverrides(overlay));
    }

    [TestCase("{\"patch\":[{\"id\":\"ai\",\"typo\":true}]}")]
    [TestCase("{\"set\":[{\"id\":\"ai\"}]}")]
    [TestCase("{\"insert_before\":[{\"before\":{\"id\":\"google\"},\"value\":null}]}")]
    public void UnselectedPlatformsStillValidateKeyedOperationStructures(string operation)
    {
        var overlay = JsonNode.Parse("{\"platforms\":{\"Windows\":{\"route\":{\"services\":" + operation + "}}}}")!.AsObject();
        Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.Resolve(TargetPlatform.Linux, overlay));
    }

    [Test]
    public void KeyedPatchCanOverrideAnOmittedOptionalFieldOnAnAppendedEntity()
    {
        var added = Entity("route", "services", "ai");
        added["id"] = "custom";
        added["name"] = "Custom service";
        added.Remove("precedes_domestic_routes");
        var overlay = ArrayOperation("route", "services", "append", added);
        overlay["route"]!["services"]!["patch"] = new JsonArray(new JsonObject
        {
            ["id"] = "custom",
            ["precedes_domestic_routes"] = true
        });

        var profile = BoxForgeProfile.Default.ApplyOverrides(overlay);

        Assert.That(profile.Services.Last().Id, Is.EqualTo("custom"));
        Assert.That(profile.Services.Last().PrecedesDomesticRoutes, Is.True);
    }

    [TestCase("{\"route\":{\"services\":{\"patch\":[{\"id\":\"sensitive-selector\",\"default_region\":\"jp\"}]}}}", "profile.request.route.services.patch[0]")]
    [TestCase("{\"platforms\":{\"Windows\":{\"route\":{\"services\":{\"patch\":[{\"id\":\"ai\",\"sensitive-key\":true}]}}}}}", "profile.request.platforms.Windows.route.services.patch[0][unknown]")]
    [TestCase("{\"features\":{\"api\":{\"enabled\":\"sensitive-value\"}}}", "profile.request.features.api.enabled")]
    public void RequestDiagnosticsIdentifyLayersAndOperationsWithoutEchoingInput(string json, string path)
    {
        var overlay = JsonNode.Parse(json)!.AsObject();
        var error = Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.Resolve(TargetPlatform.Linux, overlay))!;
        Assert.That(error.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[] { ("PF001", path) }));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("sensitive"));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("default_region"));
        Assert.Throws<NotSupportedException>(() => ((IList<ConfigDiagnostic>)error.Diagnostics).Clear());
    }

    [Test]
    public void ProfileDiagnosticsDistinguishFilesHostAndStoredPlatformLayers()
    {
        const string invalidBoolean = "{\"features\":{\"api\":{\"enabled\":\"sensitive-value\"}}}";
        var file = Assert.Throws<ProfileValidationException>(() => Load(invalidBoolean))!;
        var host = Assert.Throws<ProfileValidationException>(() => ProfileConfiguration.ResolveProfile(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["features:api:enabled"] = "sensitive-value" }).Build()))!;
        var document = BoxForgeProfile.Default.ApplyOverrides(JsonNode.Parse("""
            {"platforms":{"Linux":{"route":{"services":{"patch":[{"id":"ai","default_region":"jp"}]}}}}}
            """)!.AsObject());
        var layer = Assert.Throws<ProfileValidationException>(() => document.ApplyOverrides(JsonNode.Parse("""
            {"route":{"services":{"remove_by_key":[{"id":"ai"}]}}}
            """)!.AsObject()))!;
        Assert.Multiple(() =>
        {
            Assert.That(file.Diagnostics[0].Path, Is.EqualTo("profile.file.features.api.enabled"));
            Assert.That(host.Diagnostics[0].Path, Is.EqualTo("profile.host.features.api.enabled"));
            Assert.That(layer.Diagnostics[0].Path, Is.EqualTo("profile.platform_layers[1].Linux.route.services.patch[0]"));
            Assert.That(JsonSerializer.Serialize(file.Diagnostics.Concat(host.Diagnostics).Concat(layer.Diagnostics)), Does.Not.Contain("sensitive"));
        });
    }

    [Test]
    public void EffectiveProfileSemanticDiagnosticsUseSectionAndArrayIndex()
    {
        var error = Assert.Throws<ProfileValidationException>(() => BoxForgeProfile.Default.Resolve(TargetPlatform.Linux,
            JsonNode.Parse("""
                {"dns":{"resolvers":{"patch":[{"id":"cloudflare","server":"sensitive-address"}]}}}
                """)!.AsObject()))!;
        Assert.That(error.Diagnostics[0].Path, Is.EqualTo("profile.effective.Linux.dns.resolvers[2].server"));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("sensitive-address"));
        Assert.That(error, Is.InstanceOf<FormatException>());
    }

    private static JsonObject Resolved(ResolvedProfile profile) => JsonNode.Parse(profile.ToResolvedJson())!.AsObject();

    private static JsonObject Resolved(BoxForgeProfile profile) => JsonNode.Parse(profile.ToResolvedJson())!.AsObject();

    private static JsonObject Entity(string section, string field, string id, BoxForgeProfile? profile = null) =>
        (JsonObject)Resolved(profile ?? BoxForgeProfile.Default)[section]![field]!.AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == id)!.DeepClone();

    private static JsonObject ArrayOperation(string section, string field, string operation, JsonNode entity) => new()
    {
        [section] = new JsonObject { [field] = new JsonObject { [operation] = new JsonArray(entity.DeepClone()) } }
    };

    private static BoxForgeProfile ReplaceEntity(string section, string field, string id, JsonObject patch, BoxForgeProfile? profile = null)
    {
        profile ??= BoxForgeProfile.Default;
        var entries = (JsonArray)Resolved(profile)[section]![field]!.DeepClone();
        var entity = entries.OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == id);
        foreach (var (key, value) in patch) entity[key] = value?.DeepClone();
        return profile.ApplyOverrides(new JsonObject
        { [section] = new JsonObject { [field] = new JsonObject { ["replace"] = entries } } });
    }

    private static BoxForgeProfile ReplaceRuleSet(JsonObject patch)
    {
        var entries = (JsonArray)Resolved(BoxForgeProfile.Default)["route"]!["rule_sets"]!.DeepClone();
        var source = entries[0]!.AsObject();
        foreach (var (key, value) in patch) source[key] = value?.DeepClone();
        return BoxForgeProfile.Default.ApplyOverrides(new JsonObject
        { ["route"] = new JsonObject { ["rule_sets"] = new JsonObject { ["replace"] = entries } } });
    }

    private static BoxForgeProfile Load(string json)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return BoxForgeProfile.Load(source);
    }

    private static ServiceProvider CreateProvider(string? path, Dictionary<string, string?>? values = null)
    {
        var settings = values is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(values);
        settings["Profile"] = path;
        return new ServiceCollection().AddLogging().AddBoxForgeCore(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .BuildServiceProvider();
    }

    private static async Task<JsonDocument> Generate(BoxForgeProfile profile, TargetPlatform platform, string yaml)
    {
        using var provider = new ServiceCollection().AddLogging().AddBoxForgeCore(new ConfigurationBuilder().Build())
            .AddSingleton(profile).BuildServiceProvider();
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>().ConvertAsync(new ConversionRequest("profile", yaml, [platform]));
        return JsonDocument.Parse(bundle.Artifacts.Single().Content);
    }

    internal static string Yaml(params string[] names) => "proxies:\n" + string.Concat(names.Select(name =>
        $"  - name: {JsonSerializer.Serialize(name)}\n    type: ss\n    server: node.example.com\n    port: 443\n    cipher: aes-128-gcm\n    password: test-only\n"));
}
