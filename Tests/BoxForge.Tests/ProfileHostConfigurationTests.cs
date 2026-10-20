using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ProfileHostConfigurationTests
{
    [Test]
    public void TailscaleIsEnabledByDefaultOnEveryPlatform()
    {
        using ServiceProvider provider = CreateProvider(
            new Dictionary<string, string?>());

        var tailscale = provider.GetRequiredService<BoxForgeProfile>().Runtime.Tailscale;

        Assert.Multiple(() =>
        {
            Assert.That(tailscale.Enabled, Is.True);
            Assert.That(tailscale.AndroidEnabled, Is.True);
        });
    }

    [Test]
    public void TailscaleCanBeDisabledForDesktopAndAndroidIndependently()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Tailscale:Enabled"] = "false",
            ["Tailscale:AndroidEnabled"] = "false"
        });

        var tailscale = provider.GetRequiredService<BoxForgeProfile>().Runtime.Tailscale;

        Assert.Multiple(() =>
        {
            Assert.That(tailscale.Enabled, Is.False);
            Assert.That(tailscale.AndroidEnabled, Is.False);
        });
    }

    [Test]
    public void SingboxApiIsDisabledByDefault()
    {
        using ServiceProvider provider = CreateProvider(
            new Dictionary<string, string?>());

        var options = provider.GetRequiredService<BoxForgeProfile>().Runtime.SingboxApi;

        Assert.That(options.Enabled, Is.False);
    }

    [Test]
    public void SingboxApiCanBeEnabled()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["SingboxApi:Enabled"] = "true"
        });

        var options = provider.GetRequiredService<BoxForgeProfile>().Runtime.SingboxApi;

        Assert.That(options.Enabled, Is.True);
    }

    [Test]
    public void InvalidSingboxApiBooleanFailsDuringCoreRegistration()
    {
        Assert.That(() =>
        {
            using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
            {
                ["SingboxApi:Enabled"] = "yes"
            });
        }, Throws.TypeOf<FormatException>()
            .With.Message.EqualTo("配置项 'SingboxApi:Enabled' 必须是 true 或 false。"));
    }

    [Test]
    public void NestedEnabledKeyTakesPriorityOverLegacyKey()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Tailscale:Enabled"] = "true",
            ["TailscaleEnabled"] = "false"
        });

        var tailscale = provider.GetRequiredService<BoxForgeProfile>().Runtime.Tailscale;

        Assert.That(tailscale.Enabled, Is.True);
    }

    [Test]
    public void LegacyEnabledKeyRemainsSupported()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["TailscaleEnabled"] = "true"
        });

        var tailscale = provider.GetRequiredService<BoxForgeProfile>().Runtime.Tailscale;

        Assert.That(tailscale.Enabled, Is.True);
    }

    [Test]
    public void AndroidEnabledUsesItsOwnPlatformSetting()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Tailscale:Enabled"] = "true",
            ["Tailscale:AndroidEnabled"] = "false",
            ["TailscaleAndroidEnabled"] = "true"
        });

        var tailscale = provider.GetRequiredService<BoxForgeProfile>().Runtime.Tailscale;

        Assert.Multiple(() =>
        {
            Assert.That(tailscale.Enabled, Is.True);
            Assert.That(tailscale.AndroidEnabled, Is.False);
        });
    }

    [Test]
    public void InvalidBooleanValueFailsDuringCoreRegistration()
    {
        Assert.That(() =>
        {
            using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Tailscale:Enabled"] = "yes"
            });
        }, Throws.TypeOf<FormatException>()
            .With.Message.EqualTo("配置项 'Tailscale:Enabled' 必须是 true 或 false。"));
    }

    private static ServiceProvider CreateProvider(
        IDictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddBoxForgeCore(configuration);
        return services.BuildServiceProvider();
    }
}
