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
            Assert.That(endpoint.TaildropDirectory, Is.EqualTo(expectedDirectory));
            Assert.That(json, Does.Contain("\"taildrop_directory\":"));
            Assert.That(json, Does.Contain(expectedDirectory.Replace("\\", "\\\\")));
        });
    }

    [Test]
    public void CustomTaildropDirectoryIsTrimmed()
    {
        TailscaleEndpoint endpoint = BuildEndpoint(new TailscaleOptions
        {
            Enabled = true,
            TaildropDirectory = "  /var/lib/sing-box/taildrop  "
        }, TargetPlatform.Android);

        Assert.That(
            endpoint.TaildropDirectory,
            Is.EqualTo("/var/lib/sing-box/taildrop"));
    }

    [Test]
    public void EmptyTaildropDirectoryIsOmittedToUseTheSingboxDefault()
    {
        TailscaleEndpoint endpoint = BuildEndpoint(new TailscaleOptions
        {
            Enabled = true,
            TaildropDirectory = "   "
        }, TargetPlatform.Android);

        string json = new ConfigSerializer().Serialize(new SingboxConfig
        {
            Endpoints = [endpoint]
        });

        Assert.Multiple(() =>
        {
            Assert.That(endpoint.TaildropDirectory, Is.Null);
            Assert.That(json, Does.Not.Contain("taildrop_directory"));
        });
    }

    private static TailscaleEndpoint BuildEndpoint(
        TailscaleOptions options,
        TargetPlatform platform) =>
        (TailscaleEndpoint)new TailscaleEndpointBuilder(Options.Create(options))
            .Build(platform)
            .Single();
}
