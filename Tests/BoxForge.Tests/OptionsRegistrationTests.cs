using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class OptionsRegistrationTests
{
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
