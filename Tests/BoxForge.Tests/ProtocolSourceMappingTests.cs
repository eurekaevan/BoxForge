using System.Text.Json;
using BoxForge.Configuration;
using BoxForge.Engine;
using BoxForge.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProtocolSourceMappingTests
{
    private static IEnumerable<TestCaseData> Fixtures()
    {
        foreach (string fixture in new[] { "shadowsocks", "hysteria2", "anytls", "profiles" })
            foreach (TargetPlatform platform in Enum.GetValues<TargetPlatform>())
                yield return new TestCaseData(fixture, platform);
    }

    [TestCaseSource(nameof(Fixtures))]
    public async Task RealFixturesPreserveMappingsAndCompleteReferences(string fixture, TargetPlatform platform)
    {
        using ServiceProvider provider = new ServiceCollection().AddLogging()
            .AddBoxForge(new ConfigurationBuilder().Build()).BuildServiceProvider();
        string yaml = await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fixture + ".yaml"));
        var bundle = await provider.GetRequiredService<IBoxForgeEngine>()
            .ConvertAsync(new ConversionRequest(fixture, yaml, [platform]));
        using JsonDocument document = JsonDocument.Parse(bundle.Artifacts.Single().Content);
        JsonElement config = document.RootElement;
        var outbounds = config.GetProperty("outbounds").EnumerateArray()
            .ToDictionary(outbound => outbound.GetProperty("tag").GetString()!);
        foreach (JsonElement group in outbounds.Values.Where(outbound =>
                     outbound.GetProperty("type").GetString() is "selector" or "urltest"))
        {
            string[] members = group.GetProperty("outbounds").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.That(members, Is.All.Matches<string>(outbounds.ContainsKey));
            if (group.TryGetProperty("default", out JsonElement selected))
                Assert.That(members, Does.Contain(selected.GetString()));
            if (group.GetProperty("type").GetString() == "urltest")
                Assert.That(members, Is.All.Matches<string>(tag => outbounds[tag].GetProperty("type").GetString()
                    is "shadowsocks" or "vless" or "trojan" or "hysteria2" or "anytls"));
        }
        JsonElement tun = config.GetProperty("inbounds").EnumerateArray().Single(inbound => inbound.GetProperty("type").GetString() == "tun");
        Assert.That(tun.TryGetProperty("strict_route", out _), Is.EqualTo(platform != TargetPlatform.Android));
        Assert.That(config.GetProperty("dns").GetProperty("rules")[0].GetProperty("query_type")[0].GetString(), Is.EqualTo("AAAA"));
        Assert.That(config.GetProperty("route").GetProperty("rules").EnumerateArray()
            .Count(rule => rule.TryGetProperty("ip_version", out JsonElement version) && version.GetInt32() == 6), Is.EqualTo(2));

        switch (fixture)
        {
            case "shadowsocks":
                Assert.That(outbounds["ss-uot-v1"].GetProperty("udp_over_tcp").GetProperty("version").GetInt32(), Is.EqualTo(1));
                Assert.That(outbounds["ss-uot-v2"].GetProperty("udp_over_tcp").GetProperty("version").GetInt32(), Is.EqualTo(2));
                Assert.That(outbounds["ss-obfs-http"].GetProperty("plugin").GetString(), Is.EqualTo("obfs-local"));
                Assert.That(outbounds["ss-v2ray-ws-tls"].GetProperty("plugin_opts").GetString(), Does.EndWith(";tls"));
                break;
            case "hysteria2":
                Assert.That(outbounds["hy2-bandwidth"].GetProperty("up_mbps").GetInt32(), Is.EqualTo(100));
                Assert.That(outbounds["hy2-bandwidth"].GetProperty("down_mbps").GetInt32(), Is.EqualTo(80));
                Assert.That(outbounds["hy2-bandwidth"].TryGetProperty("bbr_profile", out _), Is.False);
                Assert.That(outbounds["hy2-hop"].TryGetProperty("hop_interval_max", out _), Is.False);
                Assert.That(outbounds["hy2-gecko"].GetProperty("obfs").GetProperty("min_packet_size").GetInt32(), Is.EqualTo(600));
                break;
            case "anytls":
                Assert.That(outbounds["anytls-session"].GetProperty("idle_session_check_interval").GetString(), Is.EqualTo("30s"));
                Assert.That(outbounds["anytls-session"].GetProperty("min_idle_session").GetInt32(), Is.EqualTo(1));
                Assert.That(outbounds["anytls-session"].GetProperty("client_metadata").GetString(), Is.EqualTo("test-only"));
                break;
            case "profiles":
                Assert.That(outbounds[SingboxTags.MainProxyGroup].GetProperty("default").GetString(), Is.EqualTo("🇯🇵 JP"));
                Assert.That(outbounds["🇯🇵 JP"].GetProperty("default").GetString(), Is.EqualTo("🇯🇵 JP AUTO"));
                foreach (ServiceDefinition service in ProfileDefinitions.Services)
                    Assert.That(outbounds[service.Name].GetProperty("default").GetString(),
                        Is.EqualTo(service.DefaultRegion switch
                        {
                            RegionId.UnitedStates => "US01",
                            RegionId.HongKong => "HK01",
                            RegionId.Japan => "🇯🇵 JP",
                            _ => SingboxTags.MainProxyGroup
                        }));
                Assert.That(outbounds.ContainsKey("🇺🇸 US AUTO"), Is.False);
                Assert.That(outbounds.ContainsKey("🇭🇰 HK AUTO"), Is.False);
                break;
        }
    }
}
