using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class TailscaleEndpointBuilderTests
{
    [TestCase(TargetPlatform.Android, "Taildrop")]
    [TestCase(TargetPlatform.Windows, "$USERPROFILE\\Downloads\\Taildrop")]
    [TestCase(TargetPlatform.Linux, "$HOME/Downloads/Taildrop")]
    public void EnabledEndpointUsesThePlatformTaildropDirectory(
        TargetPlatform platform,
        string expectedDirectory)
    {
        TailscaleEndpoint endpoint = BuildEndpoint(new TailscaleOptions
        {
            Enabled = true
        }, platform);

        string json = new ConfigSerializer().Serialize(new SingboxConfig
        {
            Endpoints = [endpoint]
        });

        Assert.Multiple(() =>
        {
            Assert.That(endpoint.Tag, Is.EqualTo(SingboxTags.TailscaleEndpoint));
            Assert.That(
                endpoint.StateDirectory,
                Is.EqualTo(SingboxTags.TailscaleStateDirectory));
            Assert.That(endpoint.AcceptRoutes, Is.True);
            Assert.That(endpoint.TaildropDirectory, Is.EqualTo(expectedDirectory));
            Assert.That(json, Does.Contain("\"taildrop_directory\":"));
            Assert.That(json, Does.Contain(expectedDirectory.Replace("\\", "\\\\")));
            Assert.That(json, Does.Not.Contain("control_url"));
            Assert.That(json, Does.Not.Contain("hostname"));
            Assert.That(json, Does.Not.Contain("exit_node"));
        });
    }

    private static TailscaleEndpoint BuildEndpoint(
        TailscaleOptions options,
        TargetPlatform platform) =>
        (TailscaleEndpoint)new TailscaleEndpointBuilder(Options.Create(options))
            .Build(platform)
            .Single();
}
