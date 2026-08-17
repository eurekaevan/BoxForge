using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class TailscaleEndpointBuilderTests
{
    [Test]
    public void EnabledEndpointUsesTheSingboxTaildropDefaultDirectory()
    {
        TailscaleEndpoint endpoint = BuildEndpoint(new TailscaleOptions
        {
            Enabled = true
        });

        string json = new ConfigSerializer().Serialize(new SingboxConfig
        {
            Endpoints = [endpoint]
        });

        Assert.Multiple(() =>
        {
            Assert.That(endpoint.TaildropDirectory, Is.EqualTo("Taildrop"));
            Assert.That(json, Does.Contain("\"taildrop_directory\": \"Taildrop\""));
        });
    }

    [Test]
    public void CustomTaildropDirectoryIsTrimmed()
    {
        TailscaleEndpoint endpoint = BuildEndpoint(new TailscaleOptions
        {
            Enabled = true,
            TaildropDirectory = "  /var/lib/sing-box/taildrop  "
        });

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
        });

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

    private static TailscaleEndpoint BuildEndpoint(TailscaleOptions options) =>
        (TailscaleEndpoint)new TailscaleEndpointBuilder(Options.Create(options))
            .Build()
            .Single();
}
