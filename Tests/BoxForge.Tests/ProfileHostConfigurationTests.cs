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
            ["platforms:Linux:tuning:connect_timeout"] = "9s"
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

    [Test]
    public void HostValuesAcceptRawDurationStrings()
    {
        using var provider = CreateProvider(new Dictionary<string, string?> { ["tuning:connect_timeout"] = "9s" });
        Assert.That(provider.GetRequiredService<BoxForgeProfile>().Resolve(TargetPlatform.Linux).Tuning.ConnectTimeout,
            Is.EqualTo("9s"));
    }

    [Test]
    public void HostPathsRejectNestedPlatformsBeforeBuildingTheOverlay()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["platforms:Linux:platforms:Windows:tuning:connect_timeout"] = "9s"
        }).Build();
        var error = Assert.Throws<ProfileValidationException>(() => ProfileConfiguration.ResolveProfile(configuration))!;
        Assert.That(error.Diagnostics.Single().Path, Is.EqualTo("profile.host"));
        Assert.That(error.Message, Does.Contain("嵌套 platforms"));
    }

    [Test]
    public void HostStringsPreserveLiteralValues(
        [Values("123", "true", "false", "null", "[]", "{}", "\"quoted\"", " TRUE ", " ordinary path ")] string value,
        [Values(false, true)] bool platformLayer)
    {
        string prefix = platformLayer ? "platforms:linux:" : "";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [prefix + "FEATURES:TAILSCALE:TAILDROP_DIRECTORY"] = value }).Build();
        var profile = ProfileConfiguration.ResolveProfile(configuration);
        Assert.That(profile.Resolve(TargetPlatform.Linux).Features.Tailscale.TaildropDirectory, Is.EqualTo(value));
        if (platformLayer)
            Assert.That(profile.Resolve(TargetPlatform.Windows).Features.Tailscale.TaildropDirectory,
                Is.EqualTo(BoxForgeProfile.Default.Resolve(TargetPlatform.Windows).Features.Tailscale.TaildropDirectory));
    }

    [Test]
    public void HostNullableStringsAcceptNull(
        [Values("tcp_keep_alive", "tcp_keep_alive_interval")] string field,
        [Values(false, true)] bool platformLayer,
        [Values("null", " null ")] string value)
    {
        string prefix = platformLayer ? "platforms:Linux:" : "";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [prefix + "tuning:" + field] = value }).Build();
        var profile = ProfileConfiguration.ResolveProfile(configuration);
        using var snapshot = JsonDocument.Parse(profile.Resolve(TargetPlatform.Linux).ToResolvedJson());
        Assert.That(snapshot.RootElement.GetProperty("tuning").GetProperty(field).ValueKind,
            Is.EqualTo(JsonValueKind.Null));
        if (platformLayer)
        {
            using var other = JsonDocument.Parse(profile.Resolve(TargetPlatform.Windows).ToResolvedJson());
            Assert.That(other.RootElement.GetProperty("tuning").GetProperty(field).ValueKind,
                Is.EqualTo(JsonValueKind.String));
        }
    }

    [Test]
    public void HostTypedValuesRetainBooleanNumberEnumObjectAndArraySemantics(
        [Values(false, true)] bool platformLayer, [Values("null", " TRUE ")] string strictRoute)
    {
        string prefix = platformLayer ? "platforms:Linux:" : "";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [prefix + "features:api"] = "{\"enabled\":true}",
            [prefix + "features:tailscale:enabled"] = " FALSE ",
            [prefix + "route:strict_route"] = strictRoute,
            [prefix + "route:sniff_excluded_ports"] = "{\"replace\":[22,3306]}",
            [prefix + "dns:address_family"] = "Auto",
            [prefix + "dns:cache_capacity"] = "1234",
            [prefix + "tuning:url_test_tolerance"] = "123"
        }).Build();
        var profile = ProfileConfiguration.ResolveProfile(configuration).Resolve(TargetPlatform.Linux);
        using var document = JsonDocument.Parse(profile.ToResolvedJson());
        JsonElement snapshot = document.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(profile.Features.Api.Enabled, Is.True);
            Assert.That(profile.Features.Tailscale.Enabled, Is.False);
            Assert.That(profile.Settings.StrictRoute, Is.EqualTo(strictRoute == "null" ? (bool?)null : true));
            Assert.That(profile.Settings.AddressFamily, Is.EqualTo(AddressFamilyPolicy.Auto));
            Assert.That(snapshot.GetProperty("dns").GetProperty("cache_capacity").GetUInt32(), Is.EqualTo(1234));
            Assert.That(profile.Tuning.UrlTestTolerance, Is.EqualTo(123));
            Assert.That(snapshot.GetProperty("route").GetProperty("sniff_excluded_ports").EnumerateArray().Select(item => item.GetInt32()),
                Is.EqualTo(new[] { 22, 3306 }));
        });
    }

    [TestCase("features:api:enabled", "123")]
    [TestCase("tuning:url_test_tolerance", "true")]
    [TestCase("tuning:url_test_tolerance", "1.5")]
    [TestCase("route:sniff_excluded_ports", "[22]")]
    [TestCase("features:api", "true")]
    [TestCase("features:api", "{invalid")]
    public void HostTypedValuesRejectInvalidTypes(string path, string value)
    {
        foreach (string prefix in new[] { "", "platforms:Linux:" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [prefix + path] = value }).Build();
            Assert.Throws<ProfileValidationException>(() => ProfileConfiguration.ResolveProfile(configuration));
        }
    }

    [TestCase("features:api:enabled", "yes")]
    [TestCase("features:tailscale:enabled", "null")]
    [TestCase("features:tailscale:enabled", "\"true\"")]
    [TestCase("dns:cache_capacity", "0")]
    [TestCase("tuning:connect_timeout", "0s")]
    [TestCase("tuning:connect_timeout", "\"9s\"")]
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
