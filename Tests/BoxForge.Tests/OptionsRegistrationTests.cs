using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class OptionsRegistrationTests
{
    [Test]
    public void NestedKeysTakePriorityOverLegacyKeys()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Singbox:MainProxyGroup"] = "nested-main",
            ["MainProxyGroup"] = "legacy-main",
            ["Tailscale:Enabled"] = "true",
            ["TailscaleEnabled"] = "false",
            ["Tailscale:Tag"] = "nested-tailnet",
            ["TailscaleTag"] = "legacy-tailnet"
        });

        SingboxOptions singbox = provider
            .GetRequiredService<IOptions<SingboxOptions>>()
            .Value;
        TailscaleOptions tailscale = provider
            .GetRequiredService<IOptions<TailscaleOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(singbox.MainProxyGroup, Is.EqualTo("nested-main"));
            Assert.That(tailscale.Enabled, Is.True);
            Assert.That(tailscale.Tag, Is.EqualTo("nested-tailnet"));
        });
    }

    [Test]
    public void LegacyKeysRemainSupported()
    {
        using ServiceProvider provider = CreateProvider(new Dictionary<string, string?>
        {
            ["Direct"] = "legacy-direct",
            ["TailscaleEnabled"] = "true",
            ["TailscaleTaildropDirectory"] = "Received"
        });

        SingboxOptions singbox = provider
            .GetRequiredService<IOptions<SingboxOptions>>()
            .Value;
        TailscaleOptions tailscale = provider
            .GetRequiredService<IOptions<TailscaleOptions>>()
            .Value;

        Assert.Multiple(() =>
        {
            Assert.That(singbox.Direct, Is.EqualTo("legacy-direct"));
            Assert.That(tailscale.Enabled, Is.True);
            Assert.That(tailscale.TaildropDirectory, Is.EqualTo("Received"));
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
