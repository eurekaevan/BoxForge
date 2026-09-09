using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class OptionsRegistrationTests
{
    [Test]
    public void TailscaleIsDisabledByDefaultOnEveryPlatform()
    {
        using ServiceProvider provider = CreateProvider(
            new Dictionary<string, string?>());

        TailscaleOptions tailscale = provider
            .GetRequiredService<IOptions<TailscaleOptions>>()
            .Value;

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

        SingboxApiOptions options = provider
            .GetRequiredService<IOptions<SingboxApiOptions>>()
            .Value;

        Assert.That(options.Enabled, Is.False);
    }

    [Test]
    public void SingboxApiCanBeEnabled()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["SingboxApi:Enabled"] = "true"
        });

        SingboxApiOptions options = provider
            .GetRequiredService<IOptions<SingboxApiOptions>>()
            .Value;

        Assert.That(options.Enabled, Is.True);
    }

    [Test]
    public void InvalidSingboxApiBooleanFailsWhenOptionsAreLoaded()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["SingboxApi:Enabled"] = "yes"
        });

        Assert.That(
            () => provider.GetRequiredService<IOptions<SingboxApiOptions>>().Value,
            Throws.TypeOf<FormatException>()
                .With.Message.EqualTo(
                    "配置项 'SingboxApi:Enabled' 必须是 true 或 false。"));
    }

    [Test]
    public void NestedEnabledKeyTakesPriorityOverLegacyKey()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Tailscale:Enabled"] = "true",
            ["TailscaleEnabled"] = "false"
        });

        TailscaleOptions tailscale = provider
            .GetRequiredService<IOptions<TailscaleOptions>>()
            .Value;

        Assert.That(tailscale.Enabled, Is.True);
    }

    [Test]
    public void LegacyEnabledKeyRemainsSupported()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["TailscaleEnabled"] = "true"
        });

        TailscaleOptions tailscale = provider
            .GetRequiredService<IOptions<TailscaleOptions>>()
            .Value;

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

        TailscaleOptions tailscale = provider
            .GetRequiredService<IOptions<TailscaleOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(tailscale.Enabled, Is.True);
            Assert.That(tailscale.AndroidEnabled, Is.False);
        });
    }

    [Test]
    public void InvalidBooleanValueFailsWhenOptionsAreLoaded()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Tailscale:Enabled"] = "yes"
        });

        Assert.That(
            () => provider.GetRequiredService<IOptions<TailscaleOptions>>().Value,
            Throws.TypeOf<FormatException>()
                .With.Message.EqualTo(
                    "配置项 'Tailscale:Enabled' 必须是 true 或 false。"));
    }

    private static ServiceProvider CreateProvider(
        IDictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddBoxForgeOptions(configuration);
        return services.BuildServiceProvider();
    }
}
