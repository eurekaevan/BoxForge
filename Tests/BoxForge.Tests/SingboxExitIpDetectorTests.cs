using System.Net;
using System.Text.Json;
using BoxForge.Configuration;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SingboxExitIpDetectorTests
{
    [Test]
    public async Task WritesMinimalProbeConfigAndCleansUpAfterSuccess()
    {
        var exitAddress = IPAddress.Parse("203.0.113.1");
        var launcher = new StubSingboxProcessLauncher();
        var fetcher = new StubExitIpFetcher(exitAddress);
        var detector = CreateDetector(launcher, fetcher);

        IPAddress? result = await detector.DetectAsync(CreateOutbound());

        Assert.That(result, Is.EqualTo(exitAddress));
        Assert.That(launcher.ConfigContent, Is.Not.Null);
        using JsonDocument document = JsonDocument.Parse(launcher.ConfigContent!);
        JsonElement root = document.RootElement;
        string[] rootProperties = [.. root.EnumerateObject()
            .Select(property => property.Name)];
        JsonElement inbound = root.GetProperty("inbounds")[0];
        JsonElement outbound = root.GetProperty("outbounds")[0];
        JsonElement route = root.GetProperty("route");

        Assert.Multiple(() =>
        {
            Assert.That(
                rootProperties,
                Is.EqualTo(new[] { "inbounds", "outbounds", "route" }));
            Assert.That(inbound.GetProperty("type").GetString(), Is.EqualTo("mixed"));
            Assert.That(
                inbound.GetProperty("listen").GetString(),
                Is.EqualTo("127.0.0.1"));
            Assert.That(
                inbound.GetProperty("listen_port").GetInt32(),
                Is.EqualTo(launcher.SocksPort));
            Assert.That(
                outbound.GetProperty("type").GetString(),
                Is.EqualTo("shadowsocks"));
            Assert.That(outbound.GetProperty("tag").GetString(), Is.EqualTo("node"));
            Assert.That(
                outbound.GetProperty("server").GetString(),
                Is.EqualTo("198.51.100.20"));
            Assert.That(outbound.TryGetProperty("domain_resolver", out _), Is.False);
            Assert.That(route.GetProperty("final").GetString(), Is.EqualTo("node"));
            Assert.That(launcher.Executable, Is.EqualTo("/opt/sing-box"));
            Assert.That(fetcher.SocksPort, Is.EqualTo(launcher.SocksPort));
            Assert.That(launcher.Process.WaitCount, Is.EqualTo(1));
            Assert.That(launcher.Process.DisposeCount, Is.EqualTo(1));
            Assert.That(File.Exists(launcher.ConfigPath), Is.False);
            Assert.That(
                Directory.Exists(Path.GetDirectoryName(launcher.ConfigPath)),
                Is.False);
        });
    }

    [Test]
    public async Task FetchFailureStillStopsProcessAndDeletesTemporaryFiles()
    {
        const string apiKey = "must-not-leak";
        var launcher = new StubSingboxProcessLauncher();
        var logger = new RecordingLogger<SingboxExitIpDetector>();
        var detector = CreateDetector(
            launcher,
            new ThrowingExitIpFetcher(),
            logger,
            apiKey);

        IPAddress? result = await detector.DetectAsync(CreateOutbound());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(launcher.Process.DisposeCount, Is.EqualTo(1));
            Assert.That(File.Exists(launcher.ConfigPath), Is.False);
            Assert.That(
                Directory.Exists(Path.GetDirectoryName(launcher.ConfigPath)),
                Is.False);
            Assert.That(logger.Messages, Has.None.Contains(apiKey));
            Assert.That(logger.Exceptions, Is.Empty);
        });
    }

    private static SingboxExitIpDetector CreateDetector(
        ISingboxProcessLauncher launcher,
        IExitIpFetcher fetcher,
        RecordingLogger<SingboxExitIpDetector>? logger = null,
        string apiKey = "") =>
        new(
            Options.Create(new NodeEnrichmentOptions
            {
                Enabled = true,
                Mode = NodeEnrichmentMode.Exit,
                SingBoxPath = "/opt/sing-box",
                Ip2LocationApiKey = apiKey
            }),
            new StubProbeServerResolver("198.51.100.20"),
            launcher,
            fetcher,
            logger ?? new RecordingLogger<SingboxExitIpDetector>());

    private static ShadowsocksOutbound CreateOutbound() =>
        new()
        {
            Tag = "node",
            Server = "node.example",
            ServerPort = 443,
            Method = "aes-128-gcm",
            Password = "secret"
        };
}
