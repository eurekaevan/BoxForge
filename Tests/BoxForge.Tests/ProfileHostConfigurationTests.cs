using System.Text.Json;
using BoxForge.Configuration;
using BoxForge.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProfileHostConfigurationTests
{
    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.Windows)]
    public void DefaultsEnableTailscaleAndDisableApiOnEveryPlatform(TargetPlatform platform)
    {
        using var provider = CreateProvider(new Dictionary<string, string?>());
        var profile = provider.GetRequiredService<BoxForgeProfile>().ForPlatform(platform);
        Assert.Multiple(() =>
        {
            Assert.That(profile.Features.Tailscale.Enabled, Is.True);
            Assert.That(profile.Features.Api.Enabled, Is.False);
        });
    }

    [Test]
    public void UnifiedKeysConfigureRootAndIndividualPlatforms()
    {
        using var provider = CreateProvider(new Dictionary<string, string?>
        {
            ["features:tailscale:enabled"] = "false",
            ["features:api:enabled"] = "true",
            ["platforms:Android:features:tailscale:enabled"] = "true",
            ["platforms:Linux:tuning:connect_timeout"] = "\"9s\""
        });
        var profile = provider.GetRequiredService<BoxForgeProfile>();
        Assert.Multiple(() =>
        {
            Assert.That(profile.ForPlatform(TargetPlatform.Linux).Features.Tailscale.Enabled, Is.False);
            Assert.That(profile.ForPlatform(TargetPlatform.Windows).Features.Tailscale.Enabled, Is.False);
            Assert.That(profile.ForPlatform(TargetPlatform.Android).Features.Tailscale.Enabled, Is.True);
            Assert.That(profile.ForPlatform(TargetPlatform.Android).Features.Api.Enabled, Is.True);
            Assert.That(profile.ForPlatform(TargetPlatform.Linux).Tuning.ConnectTimeout, Is.EqualTo("9s"));
        });
    }

    [TestCase("9s")]
    [TestCase("\"9s\"")]
    public void HostValuesAcceptRawStringsAndJsonStringScalars(string timeout)
    {
        using var provider = CreateProvider(new Dictionary<string, string?> { ["tuning:connect_timeout"] = timeout });
        Assert.That(provider.GetRequiredService<BoxForgeProfile>().ForPlatform(TargetPlatform.Linux).Tuning.ConnectTimeout,
            Is.EqualTo("9s"));
    }

    [TestCase("features:api:enabled", "yes")]
    [TestCase("features:tailscale:enabled", "null")]
    [TestCase("features:tailscale:enabled", "\"true\"")]
    [TestCase("dns:cache_capacity", "0")]
    [TestCase("tuning:connect_timeout", "0s")]
    [TestCase("route:typo", "true")]
    [TestCase("platforms:MacOS:features:api:enabled", "true")]
    [TestCase("platforms:Android:features:tailscale:android_enabled", "true")]
    public void InvalidUnifiedSettingsFailDuringRegistration(string key, string value)
    {
        Assert.Throws<FormatException>(() =>
        {
            using var provider = CreateProvider(new Dictionary<string, string?> { [key] = value });
        });
    }

    [TestCase("Tailscale:Enabled")]
    [TestCase("Tailscale:AndroidEnabled")]
    [TestCase("TailscaleEnabled")]
    [TestCase("TailscaleAndroidEnabled")]
    [TestCase("SingboxApi:Enabled")]
    [TestCase("runtime:tailscale:enabled")]
    [TestCase("policies:sniff_timeout")]
    public void LegacyConfigurationKeysAreRejected(string key)
    {
        Assert.Throws<FormatException>(() =>
        {
            using var provider = CreateProvider(new Dictionary<string, string?> { [key] = "false" });
        });
    }

    [Test]
    public void ArrayOperationsCanBeProvidedAsJsonHostValues()
    {
        using var provider = CreateProvider(new Dictionary<string, string?>
        {
            ["route:sniff_excluded_ports"] = "{\"replace\":[25,465]}"
        });
        var profile = provider.GetRequiredService<BoxForgeProfile>().ForPlatform(TargetPlatform.Linux);
        using var json = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.That(json.RootElement.GetProperty("route").GetProperty("sniff_excluded_ports").EnumerateArray()
            .Select(port => port.GetInt32()), Is.EqualTo(new[] { 25, 465 }));
    }

    private static ServiceProvider CreateProvider(IDictionary<string, string?> values) =>
        new ServiceCollection().AddBoxForgeCore(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();
}
