using BoxForge.Exceptions;
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
        var profile = provider.GetRequiredService<BoxForgeProfile>().Resolve(platform);
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
            Assert.That(profile.Resolve(TargetPlatform.Linux).Features.Tailscale.Enabled, Is.False);
            Assert.That(profile.Resolve(TargetPlatform.Windows).Features.Tailscale.Enabled, Is.False);
            Assert.That(profile.Resolve(TargetPlatform.Android).Features.Tailscale.Enabled, Is.True);
            Assert.That(profile.Resolve(TargetPlatform.Android).Features.Api.Enabled, Is.True);
            Assert.That(profile.Resolve(TargetPlatform.Linux).Tuning.ConnectTimeout, Is.EqualTo("9s"));
        });
    }

    [TestCase("9s")]
    [TestCase("\"9s\"")]
    public void HostValuesAcceptRawStringsAndJsonStringScalars(string timeout)
    {
        using var provider = CreateProvider(new Dictionary<string, string?> { ["tuning:connect_timeout"] = timeout });
        Assert.That(provider.GetRequiredService<BoxForgeProfile>().Resolve(TargetPlatform.Linux).Tuning.ConnectTimeout,
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
        Assert.Throws<ProfileValidationException>(() =>
        {
            using var provider = CreateProvider(new Dictionary<string, string?> { [key] = value });
        });
    }

    [TestCase("Tailscale:Enabled")]
    [TestCase("Tailscale:AndroidEnabled")]
    [TestCase("TailscaleEnabled")]
    [TestCase("TailscaleAndroidEnabled")]
    [TestCase("SingboxApi:Enabled")]
    public void LegacyConfigurationKeysAreRejected(string key)
    {
        Assert.Throws<ProfileValidationException>(() =>
        {
            using var provider = CreateProvider(new Dictionary<string, string?> { [key] = "false" });
        });
    }

    [Test]
    public void UnrelatedHostSectionsDoNotChangeTheDefaultProfile()
    {
        using var provider = CreateProvider(new Dictionary<string, string?>
        {
            ["runtime:tailscale:enabled"] = "false",
            ["policies:sniff_timeout"] = "host-specific-value",
            ["regions:0:id"] = "host-region",
            ["services:mode"] = "host-service",
            ["rule_sets:source"] = "host-rule-set",
            ["resolvers:address"] = "host-resolver"
        });
        Assert.That(provider.GetRequiredService<BoxForgeProfile>().ToResolvedJson(),
            Is.EqualTo(BoxForgeProfile.Default.ToResolvedJson()));
    }

    [Test]
    [NonParallelizable]
    public void CoreRegistrationUsesOnlyItsInjectedConfigurationDespiteInvalidAmbientEnvironment()
    {
        const string unknownKey = "BOXFORGE_isolation_probe_unknown";
        const string apiKey = "BOXFORGE_features__api__enabled";
        string? oldUnknown = Environment.GetEnvironmentVariable(unknownKey);
        string? oldApi = Environment.GetEnvironmentVariable(apiKey);
        try
        {
            Environment.SetEnvironmentVariable(unknownKey, "unknown-root");
            Environment.SetEnvironmentVariable(apiKey, "invalid-boolean");
            using var defaults = CreateProvider(new Dictionary<string, string?>());
            using var configured = CreateProvider(new Dictionary<string, string?> { ["features:api:enabled"] = "true" });
            Assert.Multiple(() =>
            {
                Assert.That(defaults.GetRequiredService<BoxForgeProfile>().Features.Api.Enabled, Is.False);
                Assert.That(configured.GetRequiredService<BoxForgeProfile>().Features.Api.Enabled, Is.True);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(unknownKey, oldUnknown);
            Environment.SetEnvironmentVariable(apiKey, oldApi);
        }
    }

    [Test]
    public void EnvironmentValidationAcceptsOnlyKnownRootsAndLeavesValuesToCore()
    {
        IConfiguration environment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Profile"] = "profile.json",
            ["features:api:enabled"] = "true",
            ["dns:timeout"] = "7s",
            ["route:sniff_timeout"] = "400ms",
            ["tuning:connect_timeout"] = "8s",
            ["privacy"] = "{}",
            ["platforms:Android:features:tailscale:enabled"] = "false"
        }).Build();
        Assert.DoesNotThrow(() => environment.ValidateBoxForgeEnvironment());
        IConfiguration invalidValue = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["features:api:enabled"] = "invalid-boolean" }).Build();
        Assert.DoesNotThrow(() => invalidValue.ValidateBoxForgeEnvironment());
        Assert.Throws<ProfileValidationException>(() =>
        {
            using var provider = new ServiceCollection().AddBoxForgeCore(invalidValue).BuildServiceProvider();
        });
    }

    [TestCase("unknown:enabled")]
    [TestCase("runtime:tailscale:enabled")]
    [TestCase("policies:sniff_timeout")]
    [TestCase("Tailscale:Enabled")]
    [TestCase("TailscaleEnabled")]
    [TestCase("TailscaleAndroidEnabled")]
    [TestCase("SingboxApi:Enabled")]
    [TestCase("Profile:child")]
    public void EnvironmentValidationRejectsUnknownLegacyAndStructuredProfileRoots(string key)
    {
        IConfiguration environment = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = "true" }).Build();
        Assert.Throws<ProfileValidationException>(() => environment.ValidateBoxForgeEnvironment());
    }

    [Test]
    public void CoreRejectsStructuredProfilePathsFromInjectedConfiguration()
    {
        Assert.Throws<ProfileValidationException>(() =>
        {
            using var provider = CreateProvider(new Dictionary<string, string?> { ["Profile:child"] = "profile.json" });
        });
    }

    [Test]
    public void ValidatedEnvironmentSnapshotSuppliesTheFinalCoreSettings()
    {
        IConfiguration environment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["features:api:enabled"] = "true",
            ["dns:timeout"] = "7s",
            ["platforms:Android:features:tailscale:enabled"] = "false"
        }).Build();
        environment.ValidateBoxForgeEnvironment();
        using var provider = new ServiceCollection().AddBoxForgeCore(environment).BuildServiceProvider();
        var profile = provider.GetRequiredService<BoxForgeProfile>();
        using var resolved = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.Multiple(() =>
        {
            Assert.That(profile.Features.Api.Enabled, Is.True);
            Assert.That(profile.Resolve(TargetPlatform.Android).Features.Tailscale.Enabled, Is.False);
            Assert.That(profile.Resolve(TargetPlatform.Linux).Features.Tailscale.Enabled, Is.True);
            Assert.That(resolved.RootElement.GetProperty("dns").GetProperty("timeout").GetString(),
                Is.EqualTo("7s"));
        });
    }

    [Test]
    public void ArrayOperationsCanBeProvidedAsJsonHostValues()
    {
        using var provider = CreateProvider(new Dictionary<string, string?>
        {
            ["route:sniff_excluded_ports"] = "{\"replace\":[25,465]}"
        });
        var profile = provider.GetRequiredService<BoxForgeProfile>().Resolve(TargetPlatform.Linux);
        using var json = JsonDocument.Parse(profile.ToResolvedJson());
        Assert.That(json.RootElement.GetProperty("route").GetProperty("sniff_excluded_ports").EnumerateArray()
            .Select(port => port.GetInt32()), Is.EqualTo(new[] { 25, 465 }));
    }

    private static ServiceProvider CreateProvider(IDictionary<string, string?> values) =>
        new ServiceCollection().AddBoxForgeCore(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();
}
