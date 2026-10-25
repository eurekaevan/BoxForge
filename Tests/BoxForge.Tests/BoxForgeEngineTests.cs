using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxForge.Builders;
using BoxForge.Configuration;
using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Clash;
using BoxForge.Parsers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YamlDotNet.Core;

namespace BoxForge.Tests;

[TestFixture]
public sealed class BoxForgeEngineTests
{
    private const string ValidYaml = """
        proxies:
          - name: test
            type: ss
            server: node.example.com
            port: 443
            cipher: aes-128-gcm
            password: test-only
        """;

    [TestCase("sample")]
    [TestCase("Sample")]
    [TestCase("配置名 ")]
    public async Task CacheIdentityUsesExactRequestNameAndLowercasePlatform(string name)
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var request = new ConversionRequest(name, ValidYaml, Enum.GetValues<TargetPlatform>());
        ConversionBundle first = await engine.ConvertAsync(request);
        ConversionBundle second = await engine.ConvertAsync(request);
        var identities = new List<string>();

        foreach (var artifact in first.Artifacts)
        {
            string platform = artifact.Platform switch
            {
                TargetPlatform.Android => "android",
                TargetPlatform.Linux => "linux",
                TargetPlatform.Windows => "windows",
                _ => throw new AssertionException("Unexpected platform.")
            };
            string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name + platform)));
            using var document = JsonDocument.Parse(artifact.Content);
            string actual = document.RootElement.GetProperty("experimental").GetProperty("cache_file")
                .GetProperty("cache_id").GetString()!;
            identities.Add(actual);
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(second.Artifacts.Single(item => item.Platform == artifact.Platform).Content,
                Is.EqualTo(artifact.Content));
        }

        Assert.That(identities.Distinct().Count(), Is.EqualTo(3));
        ConversionBundle renamed = await engine.ConvertAsync(request with { Name = name + "-other" });
        foreach (var artifact in renamed.Artifacts)
        {
            using var document = JsonDocument.Parse(artifact.Content);
            Assert.That(document.RootElement.GetProperty("experimental").GetProperty("cache_file")
                .GetProperty("cache_id").GetString(), Is.Not.AnyOf(identities.ToArray()));
        }
    }

    [TestCase("rename")]
    [TestCase("add")]
    [TestCase("remove")]
    [TestCase("reorder")]
    [TestCase("metadata")]
    [TestCase("connection")]
    public async Task CacheIdentitySurvivesNodeChanges(string change)
    {
        string firstNode = ValidYaml["proxies:\n".Length..];
        string secondNode = firstNode.Replace("name: test", "name: second", StringComparison.Ordinal);
        string yaml = "proxies:\n" + firstNode + "\n" + secondNode;
        string changedYaml = change switch
        {
            "rename" => yaml.Replace("name: test", "name: renamed", StringComparison.Ordinal),
            "add" => yaml + "\n" + firstNode.Replace("name: test", "name: third", StringComparison.Ordinal),
            "remove" => ValidYaml,
            "reorder" => "proxies:\n" + secondNode + "\n" + firstNode,
            "metadata" => yaml + "\n    metadata: { remaining: 9GB }",
            "connection" => yaml.Replace("node.example.com", "other.example.com", StringComparison.Ordinal),
            _ => throw new AssertionException("Unexpected mutation.")
        };
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var request = new ConversionRequest("stable", yaml, Enum.GetValues<TargetPlatform>());
        ConversionBundle before = await engine.ConvertAsync(request);
        ConversionBundle after = await engine.ConvertAsync(request with { ClashYaml = changedYaml });
        foreach (var artifact in before.Artifacts)
        {
            using var original = JsonDocument.Parse(artifact.Content);
            using var changed = JsonDocument.Parse(after.Artifacts.Single(item => item.Platform == artifact.Platform).Content);
            Assert.That(changed.RootElement.GetProperty("experimental").GetProperty("cache_file").GetProperty("cache_id").GetString(),
                Is.EqualTo(original.RootElement.GetProperty("experimental").GetProperty("cache_file").GetProperty("cache_id").GetString()));
        }
    }

    [TestCase("vless", AddressFamilyPolicy.DualStack)]
    [TestCase("trojan", AddressFamilyPolicy.DualStack)]
    [TestCase("ss", AddressFamilyPolicy.DualStack)]
    [TestCase("hysteria2", AddressFamilyPolicy.DualStack)]
    [TestCase("anytls", AddressFamilyPolicy.DualStack)]
    [TestCase("vless", AddressFamilyPolicy.Ipv4Only)]
    [TestCase("trojan", AddressFamilyPolicy.Ipv4Only)]
    [TestCase("ss", AddressFamilyPolicy.Ipv4Only)]
    [TestCase("hysteria2", AddressFamilyPolicy.Ipv4Only)]
    [TestCase("anytls", AddressFamilyPolicy.Ipv4Only)]
    public async Task SourceIpv4ConstraintOverridesDualStackWithoutChangingUnconstrainedNodes(
        string protocol, AddressFamilyPolicy family)
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var request = new ConversionRequest("node-address-family",
            AddressFamilyYaml(protocol, ("constrained", "ipv4"), ("unconstrained", null)),
            Enum.GetValues<TargetPlatform>())
        { Profile = AddressFamilyProfile(family) };
        ConversionBundle bundle = await engine.ConvertAsync(request);

        foreach (var artifact in bundle.Artifacts)
        {
            using var document = JsonDocument.Parse(artifact.Content);
            JsonElement root = document.RootElement;
            var nodes = root.GetProperty("outbounds").EnumerateArray()
                .Where(node => node.TryGetProperty("server", out _))
                .ToDictionary(node => node.GetProperty("tag").GetString()!);
            bool constrainedResolver = nodes["constrained"].TryGetProperty("domain_resolver", out var resolver);
            Assert.Multiple(() =>
            {
                Assert.That(constrainedResolver, Is.EqualTo(family == AddressFamilyPolicy.DualStack), artifact.Platform.ToString());
                Assert.That(nodes["unconstrained"].TryGetProperty("domain_resolver", out _), Is.False,
                    "Omission must inherit the platform resolver without adding a per-node restriction.");
            });
            if (family == AddressFamilyPolicy.DualStack)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(resolver.GetProperty("server").GetString(), Is.EqualTo(SingboxTags.DirectAliDns));
                    Assert.That(resolver.GetProperty("strategy").GetString(), Is.EqualTo("ipv4_only"));
                    Assert.That(resolver.GetProperty("disable_optimistic_cache").GetBoolean(), Is.True);
                    Assert.That(root.GetProperty("route").GetProperty("default_domain_resolver").TryGetProperty("strategy", out _), Is.False,
                        "The source constraint must not narrow the platform default for other nodes.");
                });
            }
            else
                Assert.That(root.GetProperty("route").GetProperty("default_domain_resolver").GetProperty("strategy").GetString(),
                    Is.EqualTo("ipv4_only"));
        }
    }

    [TestCase("vless")]
    [TestCase("trojan")]
    [TestCase("ss")]
    [TestCase("hysteria2")]
    [TestCase("anytls")]
    public async Task SourceAddressFamilyResolversBelongToEachNodeAndEachOutput(string protocol)
    {
        RecordingConfigBuilder? recordingBuilder = null;
        using ServiceProvider provider = CreateProvider(_ =>
            recordingBuilder = new RecordingConfigBuilder(new SingboxConfigBuilder()));
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var request = new ConversionRequest("isolated-resolvers",
            AddressFamilyYaml(protocol, ("first", "ipv4"), ("second", "ipv4")),
            Enum.GetValues<TargetPlatform>())
        { Profile = AddressFamilyProfile(AddressFamilyPolicy.DualStack) };
        ConversionBundle first = await engine.ConvertAsync(request);
        ConversionBundle second = await engine.ConvertAsync(request);
        DnsResolverOptions[] resolvers = recordingBuilder!.Configurations
            .SelectMany(config => config.Outbounds.OfType<ProxyOutbound>()).Select(node => node.DomainResolver!).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(resolvers, Has.Length.EqualTo(12));
            Assert.That(resolvers, Is.All.Not.Null);
            Assert.That(resolvers.Distinct(ReferenceEqualityComparer.Instance).Count(), Is.EqualTo(resolvers.Length),
                "Nodes, platforms and repeated engine builds must receive separate resolver objects.");
            Assert.That(second.Artifacts.Select(artifact => artifact.Content),
                Is.EqualTo(first.Artifacts.Select(artifact => artifact.Content)));
        });

        var unconstrained = await engine.ConvertAsync(request with
        {
            ClashYaml = AddressFamilyYaml(protocol, ("first", null), ("second", null))
        });
        foreach (var artifact in unconstrained.Artifacts)
        {
            using var document = JsonDocument.Parse(artifact.Content);
            Assert.That(document.RootElement.GetProperty("outbounds").EnumerateArray()
                .Where(node => node.TryGetProperty("server", out _))
                .Any(node => node.TryGetProperty("domain_resolver", out _)), Is.False,
                "A prior constrained request must not affect the next request's unconstrained nodes.");
        }
    }

    [TestCase("ipv6")]
    [TestCase("dual")]
    [TestCase("ipv4-prefer")]
    [TestCase("ipv6-prefer")]
    [TestCase("null")]
    public void UnsupportedSourceAddressFamiliesStillRejectEveryProtocol(string sourceFamily)
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        foreach (string protocol in new[] { "vless", "trojan", "ss", "hysteria2", "anytls" })
        {
            var request = new ConversionRequest("unsupported-family",
                AddressFamilyYaml(protocol, ("unsafe", sourceFamily)), [TargetPlatform.Linux])
            { Profile = AddressFamilyProfile(AddressFamilyPolicy.DualStack) };
            var error = Assert.ThrowsAsync<BoxForgeConversionException>(async () => await engine.ConvertAsync(request));
            Assert.That(error!.InnerException, Is.TypeOf<NodeParseException>());
            Assert.That(error.InnerException!.Message, Does.Contain("ip-version").And.Contain("unsafe"), protocol);
        }
    }

    [TestCase("metadata: &loop {next: *loop}")]
    [TestCase("metadata: &loop [*loop]")]
    [TestCase("metadata: &one {next: &two {next: *one}}")]
    public void RejectsCyclicYamlGraphsAsConversionFailures(string metadata)
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var error = Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
            await engine.ConvertAsync(new ConversionRequest("cycle", ValidYaml + "\n    " + metadata,
                [TargetPlatform.Android])));
        Assert.That(error!.InnerException, Is.TypeOf<NodeParseException>());
        Assert.That(error.InnerException!.Message, Does.Contain("循环引用"));
    }

    [Test]
    public async Task PreservesOrdinarySharedYamlAnchors()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        string yaml = ValidYaml + "\n    metadata: {first: &shared {note: example}, second: *shared}";
        ConversionBundle bundle = await engine.ConvertAsync(new ConversionRequest("shared", yaml,
            [TargetPlatform.Android]));
        Assert.That(bundle.Artifacts, Has.Count.EqualTo(1));
    }

    [Test]
    public void YamlExpansionBudgetAppliesAcrossAllProxyNodes()
    {
        var yaml = new StringBuilder(ValidYaml).Append("\n    metadata:\n      a0: &a0 [x]");
        for (int index = 1; index <= 14; index++)
            yaml.Append($"\n      a{index}: &a{index} [*a{index - 1}, *a{index - 1}]");
        var parser = new ClashParser();
        Assert.That(parser.Parse(yaml.ToString())!.Proxies, Has.Count.EqualTo(1));
        yaml.Append("\n  - name: second\n    metadata: *a14");
        var error = Assert.Throws<NodeParseException>(() => parser.Parse(yaml.ToString()));
        Assert.That(error!.Message, Does.Contain("100000"));
    }

    [Test]
    public void YamlExpansionBudgetAlsoCountsEmptyProxyMappings()
    {
        string yaml = "proxies:\n  - &empty {}" + string.Concat(Enumerable.Repeat("\n  - *empty", 100_000));
        var error = Assert.Throws<NodeParseException>(() => new ClashParser().Parse(yaml));
        Assert.That(error!.Message, Does.Contain("100000"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void YamlExpansionBudgetAlsoBoundsRepeatedText(bool longKey)
    {
        string text = new('x', 1024 * 1024);
        string shared = longKey ? $"{{'{text}': value}}" : $"{{key: '{text}'}}";
        var yaml = new StringBuilder(ValidYaml).Append("\n    metadata:\n      a0: &shared ").Append(shared);
        for (int index = 1; index <= 16; index++)
            yaml.Append($"\n      a{index}: *shared");
        var error = Assert.Throws<NodeParseException>(() => new ClashParser().Parse(yaml.ToString()));
        Assert.That(error!.Message, Does.Contain("UTF-8 字节"));
    }

    [Test]
    public void RejectsExcessiveYamlNestingWithoutTerminatingTheProcess()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        string yaml = ValidYaml + "\n    metadata: " + new string('[', 80) + "x" + new string(']', 80);
        Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
            await engine.ConvertAsync(new ConversionRequest("deep", yaml, [TargetPlatform.Android])));
    }

    [Test]
    public void StrictGenerationRejectsUnsupportedTransportBeforeBuildingArtifacts()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        const string yaml = """
            proxies:
              - name: unsafe-vless
                type: vless
                server: node.example.com
                port: 443
                uuid: 00000000-0000-4000-8000-000000000001
                network: xhttp
            """;

        BoxForgeConversionException? error = Assert.ThrowsAsync<BoxForgeConversionException>(
            async () => await engine.ConvertAsync(new ConversionRequest(
                "unsafe", yaml, [TargetPlatform.Android, TargetPlatform.Linux])));

        Assert.Multiple(() =>
        {
            Assert.That(error!.InnerException?.Message, Does.Contain("VLESS"));
            Assert.That(error.InnerException?.Message, Does.Contain("unsafe-vless"));
            Assert.That(error.InnerException?.Message, Does.Contain("network"));
        });
    }

    [Test]
    public async Task ConvertsSinglePlatformInMemory()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionBundle bundle = await engine.ConvertAsync(
            new ConversionRequest(
                "single",
                ValidYaml,
                [TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(bundle.Name, Is.EqualTo("single"));
            Assert.That(bundle.Artifacts, Has.Count.EqualTo(1));
            Assert.That(
                bundle.Artifacts[0].Platform,
                Is.EqualTo(TargetPlatform.Android));
            Assert.That(bundle.Artifacts[0].Content, Does.Contain("\"outbounds\""));
        });
    }

    [Test]
    public async Task ConvertsEachOfThreePlatformsExactlyOnce()
    {
        RecordingConfigBuilder? recordingBuilder = null;
        using ServiceProvider provider = CreateProvider(_ =>
            recordingBuilder = new RecordingConfigBuilder(
                new SingboxConfigBuilder()));
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionBundle bundle = await engine.ConvertAsync(
            new ConversionRequest(
                "all",
                ValidYaml,
                [
                    TargetPlatform.Android,
                    TargetPlatform.Linux,
                    TargetPlatform.Windows
                ]));

        Assert.Multiple(() =>
        {
            Assert.That(bundle.Artifacts, Has.Count.EqualTo(3));
            Assert.That(
                recordingBuilder!.Calls,
                Is.EqualTo(new[]
                {
                    TargetPlatform.Android,
                    TargetPlatform.Linux,
                    TargetPlatform.Windows
                }));
        });
    }

    [Test]
    public async Task UsesDeterministicPlatformOrder()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionBundle bundle = await engine.ConvertAsync(
            new ConversionRequest(
                "ordered",
                ValidYaml,
                [
                    TargetPlatform.Windows,
                    TargetPlatform.Android,
                    TargetPlatform.Linux
                ]));

        Assert.That(
            bundle.Artifacts.Select(artifact => artifact.Platform),
            Is.EqualTo(new[]
            {
                TargetPlatform.Android,
                TargetPlatform.Linux,
                TargetPlatform.Windows
            }));
    }

    [Test]
    public async Task ComputesLowercaseSha256FromUtf8Content()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        ConversionArtifact artifact = (await engine.ConvertAsync(
            new ConversionRequest(
                "hash",
                ValidYaml,
                [TargetPlatform.Linux])))
            .Artifacts.Single();
        string expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(artifact.Content)));

        Assert.Multiple(() =>
        {
            Assert.That(artifact.Sha256, Is.EqualTo(expectedHash));
            Assert.That(artifact.Sha256, Does.Match("^[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void RejectsEmptyName()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                " ",
                ValidYaml,
                [TargetPlatform.Android])));
    }

    [Test]
    public void RejectsEmptyYaml()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                "empty-yaml",
                " ",
                [TargetPlatform.Android])));
    }

    [Test]
    public void RejectsEmptyPlatformList()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest("empty-platforms", ValidYaml, [])));
    }

    [TestCase(AddressFamilyPolicy.Auto, "ipv4")]
    [TestCase(AddressFamilyPolicy.Auto, null)]
    [TestCase(AddressFamilyPolicy.DualStack, "ipv4")]
    [TestCase(AddressFamilyPolicy.DualStack, null)]
    public void DuplicateNodeNamesRetainTheTargetValidationErrorContract(AddressFamilyPolicy family, string? secondFamily)
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var request = new ConversionRequest("duplicates",
            AddressFamilyYaml("trojan", ("duplicate", "ipv4"), ("duplicate", secondFamily)), [TargetPlatform.Linux])
        { Profile = AddressFamilyProfile(family) };
        var error = Assert.ThrowsAsync<BoxForgePlatformConversionException>(async () => await engine.ConvertAsync(request));
        Assert.That(error!.Platform, Is.EqualTo(TargetPlatform.Linux));
        Assert.That(error.InnerException, Is.TypeOf<ConfigValidationException>());
        Assert.That(((ConfigValidationException)error.InnerException!).Diagnostics,
            Has.Some.Matches<ConfigDiagnostic>(diagnostic => diagnostic.Code == "SB009" && diagnostic.Message == "标签重复。"));
    }

    [Test]
    public void TargetDiagnosticsDoNotEchoSensitiveDuplicateNodeNames()
    {
        const string sensitiveName = "sensitive-credential\n]name";
        string quoted = JsonSerializer.Serialize(sensitiveName);
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var error = Assert.ThrowsAsync<BoxForgePlatformConversionException>(async () => await engine.ConvertAsync(
            new ConversionRequest("safe-target", AddressFamilyYaml("trojan", (quoted, "ipv4"), (quoted, "ipv4")), [TargetPlatform.Linux])))!;
        Assert.That(error.Diagnostics, Has.Some.Matches<ConfigDiagnostic>(item => item.Code == "SB009" && item.Message == "标签重复。"));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("sensitive"));
        Assert.That(error.Diagnostics.Select(item => item.Path), Is.All.Not.Contains("name"));
    }

    [Test]
    public void RejectsDuplicatePlatforms()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                "duplicates",
                ValidYaml,
                [TargetPlatform.Android, TargetPlatform.Android])));
    }

    [Test]
    public void RejectsUndefinedPlatform()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        Assert.ThrowsAsync<ArgumentException>(() => engine.ConvertAsync(
            new ConversionRequest(
                "undefined",
                ValidYaml,
                [(TargetPlatform)999])));
    }

    [TestCase("proxies:\n  - name: [", "SRC001", "source")]
    [TestCase("proxies:\n  - name: sensitive-name\n    type: sensitive-type", "SRC002", "source.proxies[0].type")]
    public void ConversionErrorsExposeSafeDiagnosticsWithoutChangingTheirCategory(string yaml, string code, string path)
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var error = Assert.ThrowsAsync<BoxForgeConversionException>(async () => await engine.ConvertAsync(
            new ConversionRequest("diagnostics", yaml, [TargetPlatform.Linux])))!;
        Assert.That(error.Diagnostics.Select(item => (item.Code, item.Path)), Is.EqualTo(new[] { (code, path) }));
        Assert.That(JsonSerializer.Serialize(error.Diagnostics), Does.Not.Contain("sensitive"));
    }

    [Test]
    public void WrapsInvalidYamlInStableConversionException()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();

        BoxForgeConversionException? exception =
            Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
                await engine.ConvertAsync(new ConversionRequest(
                "invalid-yaml",
                "proxies:\n  - name: [",
                [TargetPlatform.Android])));

        Assert.That(exception!.InnerException, Is.Not.Null);
    }

    [Test]
    public void RejectsDuplicateYamlKeys()
    {
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        const string yaml = """
            proxies:
              - name: duplicate-key
                type: ss
                server: first.example.com
                server: second.example.com
                port: 443
                cipher: aes-128-gcm
                password: test-only
            """;

        BoxForgeConversionException? exception =
            Assert.ThrowsAsync<BoxForgeConversionException>(async () =>
                await engine.ConvertAsync(new ConversionRequest(
                    "duplicate-key",
                    yaml,
                    [TargetPlatform.Android])));

        Assert.That(exception!.InnerException, Is.Not.Null);
    }

    [TestCase(TargetPlatform.Android)]
    [TestCase(TargetPlatform.Linux)]
    public void PlatformReservedNamesBindAfterOneSourceParseAndPreserveFailureClassification(TargetPlatform conflictingPlatform)
    {
        var parser = new CountingParser();
        RecordingConfigBuilder? recording = null;
        using ServiceProvider provider = CreateProvider(
            _ => recording = new RecordingConfigBuilder(new SingboxConfigBuilder()), parser);
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var overlay = new JsonObject
        {
            ["platforms"] = new JsonObject
            {
                [conflictingPlatform.ToString()] = JsonNode.Parse("""
                    {"route":{"services":{"patch":[{"id":"ai","name":"test"}]}}}
                    """)
            }
        };
        var error = Assert.CatchAsync(async () => await engine.ConvertAsync(new ConversionRequest(
            "platform-name-binding", ValidYaml, [TargetPlatform.Android, TargetPlatform.Linux, TargetPlatform.Windows])
        { Profile = overlay }));
        Assert.Multiple(() =>
        {
            Assert.That(parser.Calls, Is.EqualTo(1));
            Assert.That(error!.InnerException, Is.TypeOf<NodeParseException>());
            Assert.That(error.InnerException!.Message, Is.EqualTo("节点名称 'test' 与 BoxForge 保留 tag 冲突"));
            Assert.That(recording!.Calls, Is.EqualTo(conflictingPlatform == TargetPlatform.Android
                ? Array.Empty<TargetPlatform>() : new[] { TargetPlatform.Android }));
        });
        if (conflictingPlatform == TargetPlatform.Android)
            Assert.That(error, Is.TypeOf<BoxForgeConversionException>());
        else
        {
            Assert.That(error, Is.TypeOf<BoxForgePlatformConversionException>());
            Assert.That(((BoxForgePlatformConversionException)error!).Platform, Is.EqualTo(TargetPlatform.Linux));
        }
    }

    [Test]
    public void PlatformFailureDoesNotReturnPartialBundle()
    {
        RecordingConfigBuilder? recordingBuilder = null;
        using ServiceProvider provider = CreateProvider(_ =>
            recordingBuilder = new RecordingConfigBuilder(
                new SingboxConfigBuilder(),
                TargetPlatform.Linux));
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        ConversionBundle? bundle = null;

        BoxForgePlatformConversionException? exception =
            Assert.ThrowsAsync<BoxForgePlatformConversionException>(async () =>
            bundle = await engine.ConvertAsync(new ConversionRequest(
                "failure",
                ValidYaml,
                [
                    TargetPlatform.Android,
                    TargetPlatform.Linux,
                    TargetPlatform.Windows
                ])));

        Assert.Multiple(() =>
        {
            Assert.That(bundle, Is.Null);
            Assert.That(
                exception!.Platform,
                Is.EqualTo(TargetPlatform.Linux));
            Assert.That(
                exception.InnerException,
                Is.TypeOf<ConfigValidationException>());
            Assert.That(
                recordingBuilder!.Calls,
                Is.EqualTo(new[]
                {
                    TargetPlatform.Android,
                    TargetPlatform.Linux
                }));
        });
    }

    [Test]
    public void PlanFailurePreservesStructuredDiagnosticsAndStopsPlatformGeneration()
    {
        InvalidPlanConfigBuilder? recording = null;
        using ServiceProvider provider = CreateProvider(_ =>
            recording = new InvalidPlanConfigBuilder(new SingboxConfigBuilder()));
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        ConversionBundle? bundle = null;
        var error = Assert.ThrowsAsync<BoxForgePlatformConversionException>(async () =>
            bundle = await engine.ConvertAsync(new ConversionRequest("plan-failure", ValidYaml,
                [TargetPlatform.Android, TargetPlatform.Linux, TargetPlatform.Windows])))!;
        Assert.That(error.InnerException, Is.TypeOf<ConfigValidationException>());
        var diagnostics = ((ConfigValidationException)error.InnerException!).Diagnostics;
        Assert.Multiple(() =>
        {
            Assert.That(bundle, Is.Null);
            Assert.That(error.Platform, Is.EqualTo(TargetPlatform.Linux));
            Assert.That(recording!.Calls, Is.EqualTo(new[] { TargetPlatform.Android, TargetPlatform.Linux }));
            Assert.That(diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Path)), Is.EqualTo(new[]
            {
                ("IR004", "plan.dns.tailscale_enabled"),
                ("IR004", "plan.route.tailscale_enabled")
            }));
        });
    }

    [TestCase("proxies: null", "proxies")]
    [TestCase("proxies: [null]", "proxies[0]")]
    public void RejectsNullProxyStructuresWithNodeDiagnostics(string yaml, string location)
    {
        var error = Assert.Throws<NodeParseException>(() => new ClashParser().Parse(yaml));
        Assert.That(error!.Message, Does.Contain(location).And.Contain("null"));
        using ServiceProvider provider = CreateProvider();
        var engine = provider.GetRequiredService<IBoxForgeEngine>();
        var wrapped = Assert.ThrowsAsync<BoxForgeConversionException>(() => engine.ConvertAsync(
            new ConversionRequest("invalid", yaml, [TargetPlatform.Android])));
        Assert.That(wrapped!.InnerException, Is.TypeOf<NodeParseException>());
    }

    [Test]
    public async Task ConcurrentParserInstancesKeepAliasesAndFailuresLocalToEachDocument()
    {
        ClashParser[] parsers = [new(), new(), new(), new()];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            ClashParser parser = parsers[worker % parsers.Length];
            for (int iteration = 0; iteration < 24; iteration++)
            {
                string marker = $"document-{worker}-{iteration}";
                string yaml = $$"""
                    proxies:
                      - name: {{marker}}
                        metadata:
                          first: &shared {marker: {{marker}}}
                          second: *shared
                    """;
                Assert.That(() => parser.Parse(yaml + "\n    name: duplicate"),
                    Throws.InstanceOf<YamlException>().With.Message.Contains("duplicate key"));
                Assert.That(() => parser.Parse("proxies:\n  - name: orphan\n    metadata: *shared"),
                    Throws.InstanceOf<YamlException>());

                var config = parser.Parse(yaml)!;
                var node = config.Proxies.Single();
                var metadata = node.GetObject("metadata")!;
                Assert.Multiple(() =>
                {
                    Assert.That(node.Name, Is.EqualTo(marker));
                    Assert.That(metadata.GetObject("first")!.GetString("marker"), Is.EqualTo(marker));
                    Assert.That(metadata.GetObject("second")!.GetString("marker"), Is.EqualTo(marker));
                });
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
    }

    private static JsonObject AddressFamilyProfile(AddressFamilyPolicy family) => JsonSerializer.SerializeToNode(new
    {
        platforms = new
        {
            Android = new { dns = new { address_family = family.ToString() } },
            Linux = new { dns = new { address_family = family.ToString() } },
            Windows = new { dns = new { address_family = family.ToString() } }
        }
    })!.AsObject();

    private static string AddressFamilyYaml(string protocol, params (string Name, string? Family)[] nodes) =>
        "proxies:\n" + string.Concat(nodes.Select(node =>
            $"  - name: {node.Name}\n    type: {protocol}\n    server: node.example.com\n    port: 443\n"
            + (protocol == "vless" ? "    uuid: 00000000-0000-4000-8000-000000000001\n" : "    password: test-only\n")
            + (protocol == "ss" ? "    cipher: aes-128-gcm\n" : "")
            + (node.Family is null ? "" : $"    ip-version: {node.Family}\n")));

    private static ServiceProvider CreateProvider(
        Func<IServiceProvider, ISingboxConfigBuilder>? configBuilderFactory = null,
        IClashParser? parser = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBoxForgeCore(configuration);

        if (configBuilderFactory != null)
        {
            services.RemoveAll<ISingboxConfigBuilder>();
            services.AddSingleton(configBuilderFactory);
        }

        if (parser is not null)
        {
            services.RemoveAll<IClashParser>();
            services.AddSingleton(parser);
        }

        return services.BuildServiceProvider();
    }

    private sealed class CountingParser : IClashParser
    {
        public int Calls { get; private set; }
        public ClashConfig? Parse(string yamlContent)
        {
            Calls++;
            return new ClashParser().Parse(yamlContent);
        }
    }

    internal sealed class InvalidPlanConfigBuilder(SingboxConfigBuilder inner) : ISingboxConfigBuilder
    {
        public List<TargetPlatform> Calls { get; } = [];

        public SingboxConfig Build(ConfigurationPlan plan, string? cacheId)
        {
            Calls.Add(plan.Platform);
            if (plan.Platform == TargetPlatform.Linux)
            {
                plan = plan with { TailscaleEndpointEnabled = false };
                plan.Validate();
            }
            return inner.Build(plan, cacheId);
        }
    }

    private sealed class RecordingConfigBuilder(
        ISingboxConfigBuilder inner,
        TargetPlatform? failingPlatform = null) : ISingboxConfigBuilder
    {
        public List<TargetPlatform> Calls { get; } = [];
        public List<SingboxConfig> Configurations { get; } = [];

        public SingboxConfig Build(ConfigurationPlan plan, string? cacheId)
        {
            Calls.Add(plan.Platform);
            if (plan.Platform == failingPlatform)
            {
                throw new ConfigValidationException([
                    new ConfigDiagnostic("TEST", "outbounds", $"模拟 {plan.Platform} 校验失败。")]);
            }

            SingboxConfig config = inner.Build(plan, cacheId);
            Configurations.Add(config);
            return config;
        }
    }
}
