using System.Text.Json;
using BoxForge.Models;

namespace BoxForge.Tests;

public sealed class GeneratedConfigTests
{
    [Theory]
    [InlineData(TargetPlatform.Android, 1400, false)]
    [InlineData(TargetPlatform.Linux, null, true)]
    [InlineData(TargetPlatform.Windows, null, true)]
    public void Convert_UsesPlatformSpecificInboundAndService(
        TargetPlatform platform,
        int? expectedMtu,
        bool expectsApiService)
    {
        using JsonDocument document = JsonDocument.Parse(
            TestInfrastructure.Convert(
                TestInfrastructure.ValidShadowsocksYaml,
                platform));
        JsonElement root = document.RootElement;
        JsonElement tun = Assert.Single(
            root.GetProperty("inbounds").EnumerateArray(),
            inbound => inbound.GetProperty("tag").GetString() == "tun-in");

        Assert.Equal(expectedMtu.HasValue, tun.TryGetProperty("mtu", out var mtu));
        if (expectedMtu.HasValue)
        {
            Assert.Equal(expectedMtu.Value, mtu.GetInt32());
        }

        Assert.Equal(expectsApiService, root.TryGetProperty("services", out _));
    }

    [Fact]
    public void Convert_EmitsExactDnsRaceFallbackOrder()
    {
        using JsonDocument document = JsonDocument.Parse(
            TestInfrastructure.Convert(
                TestInfrastructure.ValidShadowsocksYaml,
                TargetPlatform.Android));
        JsonElement[] rules = document.RootElement
            .GetProperty("dns")
            .GetProperty("rules")
            .EnumerateArray()
            .ToArray();

        AssertRace(rules, "cn", "local-tencent", "local");
        AssertRace(rules, "global", "remote-google", "remote");
    }

    [Fact]
    public void Convert_UsesFullConfigIdentityAndNonConstantApiSecret()
    {
        string defaultJson = TestInfrastructure.Convert(
            TestInfrastructure.ValidShadowsocksYaml,
            TargetPlatform.Linux);
        string customJson = TestInfrastructure.Convert(
            TestInfrastructure.ValidShadowsocksYaml,
            TargetPlatform.Linux,
            new Dictionary<string, string?>
            {
                ["Singbox:MainProxyGroup"] = "CUSTOM",
                ["Singbox:ApiSecret"] = "explicit-secret"
            });
        using JsonDocument defaultDocument = JsonDocument.Parse(defaultJson);
        using JsonDocument customDocument = JsonDocument.Parse(customJson);
        string defaultCacheId = GetCacheId(defaultDocument.RootElement);
        string customCacheId = GetCacheId(customDocument.RootElement);
        string generatedSecret = Assert.Single(defaultDocument.RootElement
                .GetProperty("services")
                .EnumerateArray())
            .GetProperty("secret")
            .GetString()!;

        Assert.Equal(64, defaultCacheId.Length);
        Assert.Equal(64, customCacheId.Length);
        Assert.NotEqual(defaultCacheId, customCacheId);
        Assert.Equal(32, generatedSecret.Length);
        Assert.NotEqual("127001", generatedSecret);
        Assert.Equal(
            "explicit-secret",
            Assert.Single(customDocument.RootElement
                    .GetProperty("services")
                    .EnumerateArray())
                .GetProperty("secret")
                .GetString());
    }

    private static string GetCacheId(JsonElement root) => root
        .GetProperty("experimental")
        .GetProperty("cache_file")
        .GetProperty("cache_id")
        .GetString()!;

    private static void AssertRace(
        JsonElement[] rules,
        string prefix,
        string firstServer,
        string secondServer)
    {
        string firstTag = $"{prefix}-first";
        string secondTag = $"{prefix}-second";
        int start = Array.FindIndex(
            rules,
            rule => rule.TryGetProperty("tag", out JsonElement tag)
                && tag.GetString() == firstTag);
        Assert.True(start >= 0);
        JsonElement[] race = rules.Skip(start).Take(8).ToArray();

        AssertRule(race[0], "evaluate", server: firstServer, tag: firstTag);
        AssertRule(race[1], "respond", match: firstTag, validAddress: true, isRace: true);
        AssertRule(race[2], "evaluate", server: secondServer, tag: secondTag, speculative: true);
        AssertRule(race[3], "respond", match: secondTag, validAddress: true, isRace: true);
        AssertRule(race[4], "respond", match: firstTag, responseCode: "NXDOMAIN");
        AssertRule(race[5], "respond", match: secondTag, responseCode: "NXDOMAIN");
        AssertRule(race[6], "respond", match: secondTag);
        AssertRule(race[7], "route", server: secondServer);
    }

    private static void AssertRule(
        JsonElement rule,
        string action,
        string? server = null,
        string? tag = null,
        string? match = null,
        string? responseCode = null,
        bool? validAddress = null,
        bool? isRace = null,
        bool? speculative = null)
    {
        Assert.Equal(action, rule.GetProperty("action").GetString());
        AssertOptional(rule, "server", server);
        AssertOptional(rule, "tag", tag);
        AssertOptional(rule, "match_response", match);
        AssertOptional(rule, "response_rcode", responseCode);
        AssertOptional(rule, "ip_accept_any", validAddress);
        AssertOptional(rule, "race", isRace);
        AssertOptional(rule, "speculative", speculative);
    }

    private static void AssertOptional(
        JsonElement element,
        string propertyName,
        string? expected)
    {
        Assert.Equal(
            expected != null,
            element.TryGetProperty(propertyName, out JsonElement property));
        if (expected != null)
        {
            Assert.Equal(expected, property.GetString());
        }
    }

    private static void AssertOptional(
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
}
