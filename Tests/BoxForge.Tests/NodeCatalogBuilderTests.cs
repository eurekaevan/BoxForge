using System.Collections;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using Microsoft.Extensions.Logging;
using BoxForge.Builders;
using BoxForge.Models;
using BoxForge.Models.Intermediate;
using BoxForge.Models.Singbox;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeCatalogBuilderTests
{
    [Test]
    public void CatalogRetainsAllProtocolIrAndSourceEvidence()
    {
        var builder = new NodeCatalogBuilder(
            [new VlessConverter(), new TrojanConverter(), new AnyTlsConverter(), new Hysteria2Converter(), new ShadowsocksConverter()],
            new RecordingLogger<NodeCatalogBuilder>(), BoxForgeProfile.Default);
        var catalog = builder.Build(new ClashConfig
        {
            Proxies = [CreateProxy("vless"), CreateProxy("trojan"), CreateProxy("anytls"), CreateProxy("hysteria2"), CreateProxy("ss")]
        }, strictNodeValidation: true);
        Assert.That(catalog.Entries, Is.All.TypeOf<ParsedNode>());
        Assert.That(catalog.Entries!.Cast<ParsedNode>().Select(entry => entry.Node.GetType()),
            Is.EqualTo(new[] { typeof(VlessNode), typeof(TrojanNode), typeof(AnyTlsNode), typeof(Hysteria2Node), typeof(ShadowsocksNode) }));
        var source = (ShadowsocksNode)((ParsedNode)catalog.Entries![^1]).Node;
        Assert.That(source.Cipher.Field, Is.EqualTo("cipher"));
        Assert.That(source.Cipher.RuleId, Is.EqualTo(ShadowsocksSemantics.Cipher.Id));
        Assert.That(source.Connection.ConnectTimeout, Is.Null);
        Assert.That(catalog.Names, Is.EqualTo(new[] { "vless-node", "trojan-node", "anytls-node", "hysteria2-node", "ss-node" }));
    }

    [Test]
    public void FrontendCatalogDoesNotCallLegacyConvertCore()
    {
        var builder = new NodeCatalogBuilder([new FrontendOnlyConverter()],
            new RecordingLogger<NodeCatalogBuilder>(), BoxForgeProfile.Default);
        var catalog = builder.Build(new ClashConfig { Proxies = [CreateProxy("vless")] }, strictNodeValidation: true);
        Assert.That(catalog.Entries!.Single(), Is.TypeOf<ParsedNode>());
        Assert.That(catalog.Emit(BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)).Single().ConnectTimeout, Is.EqualTo("5s"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CatalogPreservesLegacyAndReimplementedInterfaceDispatch(bool derived)
    {
        IProxyConverter legacy = derived ? new LegacyBaseConverter() : new LegacyConverter();
        var builder = new NodeCatalogBuilder([new VlessConverter(), legacy],
            new RecordingLogger<NodeCatalogBuilder>(), BoxForgeProfile.Default);
        var catalog = builder.Build(new ClashConfig
        {
            Proxies = [CreateProxy("vless"), CreateProxy("legacy")]
        }, strictNodeValidation: true);
        Assert.That(catalog.Entries![0], Is.TypeOf<ParsedNode>());
        Assert.That(catalog.Entries[1], Is.TypeOf<ConvertedNode>());
        Assert.That(catalog.Names, Is.EqualTo(new[] { "vless-node", "legacy-interface" }));
        var emitted = catalog.Emit(BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)).ToArray();
        Assert.That(emitted.Select(node => node.Tag), Is.EqualTo(catalog.Names));
        Assert.That(emitted.Select(node => node.ConnectTimeout), Is.All.EqualTo("5s"));
    }

    [Test]
    public void CatalogPreservesCachedViewDeconstructionWithAndRecordEquality()
    {
        var builder = new NodeCatalogBuilder([new TrojanConverter()],
            new RecordingLogger<NodeCatalogBuilder>(), BoxForgeProfile.Default);
        var catalog = builder.Build(new ClashConfig { Proxies = [CreateProxy("trojan")] }, strictNodeValidation: true);
        var view = catalog.Outbounds;
        var (outbounds, names) = catalog;
        Assert.That(outbounds, Is.SameAs(view));
        Assert.That(catalog.Outbounds, Is.SameAs(view));
        Assert.That((catalog with { Names = names }).Outbounds, Is.SameAs(view));
        Assert.That(new NodeCatalog(view, names), Is.EqualTo(new NodeCatalog(view, names)));
        Assert.That(new NodeCatalog(view, names).GetHashCode(), Is.EqualTo(new NodeCatalog(view, names).GetHashCode()));
        ProxyOutbound[] replacement = [((TrojanOutbound)view[0]) with { Tag = "replacement", ConnectTimeout = "9s" }];
        var replaced = catalog with { Outbounds = replacement, Names = ["replacement"] };
        Assert.That(replaced.Entries, Is.Null);
        Assert.That(replaced.Emit(BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)).Single().ConnectTimeout, Is.EqualTo("9s"));
        Assert.That(catalog.Names, Is.EqualTo(new[] { "trojan-node" }));
    }

    [Test]
    public void ModifyingLegacyTargetViewStillAffectsLaterBuildWithoutChangingSourceIr()
    {
        var proxy = new ClashProxyNode(new Hashtable
        {
            ["name"] = "trojan-node",
            ["type"] = "trojan",
            ["server"] = "node.example.com",
            ["port"] = 443,
            ["password"] = "test-only",
            ["alpn"] = new[] { "h2" }
        });
        var builder = new NodeCatalogBuilder([new TrojanConverter()],
            new RecordingLogger<NodeCatalogBuilder>(), BoxForgeProfile.Default);
        var catalog = builder.Build(new ClashConfig { Proxies = [proxy] }, strictNodeValidation: true);
        ((TrojanOutbound)catalog.Outbounds[0]).Tls!.Alpn!.Clear();
        var emitted = (TrojanOutbound)catalog.Emit(BoxForgeProfile.Default.GetPlatform(TargetPlatform.Linux)).Single();
        Assert.That(emitted.Tls!.Alpn, Is.Empty);
        var source = (TrojanNode)((ParsedNode)catalog.Entries!.Single()).Node;
        Assert.That(source.Tls.ApplicationProtocols.Value, Is.EqualTo(new[] { "h2" }));
    }

    private static ClashProxyNode CreateProxy(string type)
    {
        var fields = new Hashtable
        {
            ["type"] = type,
            ["name"] = type + "-node",
            ["server"] = "node.example.com",
            ["port"] = 443,
            [type == "vless" ? "uuid" : "password"] = "test-only"
        };
        if (type == "ss") fields["cipher"] = "aes-128-gcm";
        return new ClashProxyNode(fields);
    }

    private sealed class FrontendOnlyConverter() : ProxyConverterBase("VLESS", "vless"), INodeFrontend
    {
        protected override SourceFieldSchema Schema => VlessFrontend.Schema;
        NodeConversionResult INodeFrontend.Parse(ClashProxyNode proxy) => ParseForCatalog(proxy);
        internal override VlessNode ParseCore(ClashProxyNode node, string name) => VlessFrontend.ParseValidated(node, name);
        protected override ProxyOutbound ConvertCore(ClashProxyNode node, string name) =>
            throw new InvalidOperationException("Catalog must not call the legacy emission entry.");
    }

    private sealed class LegacyConverter : IProxyConverter
    {
        public bool CanHandle(string proxyType) => proxyType == "legacy";
        public NodeConversionResult Convert(ClashProxyNode proxy) => NodeConversionResult.Success(LegacyOutbound(proxy));
    }

    private sealed class LegacyBaseConverter() : ProxyConverterBase("Legacy", "legacy"), IProxyConverter
    {
        protected override SourceFieldSchema Schema => SourceFieldSchemas.Common().Mapped("password");
        NodeConversionResult IProxyConverter.Convert(ClashProxyNode proxy) => NodeConversionResult.Success(LegacyOutbound(proxy));
        protected override ProxyOutbound ConvertCore(ClashProxyNode node, string name) =>
            throw new InvalidOperationException("Custom interface dispatch must not be bypassed.");
    }

    private static ProxyOutbound LegacyOutbound(ClashProxyNode node) => new TrojanOutbound
    {
        Tag = "legacy-interface",
        Server = node.GetRequiredString("server"),
        ServerPort = node.GetRequiredInt("port"),
        Password = node.GetRequiredRawString("password")
    };

    [Test]
    public void StrictValidationDoesNotLogInputDerivedNodeDetails()
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder(
            [new ShadowsocksConverter()],
            logger, BoxForgeProfile.Default);
        var config = new ClashConfig
        {
            Proxies =
            [
                new ClashProxyNode(new Hashtable
                {
                    ["name"] = "sensitive-node-name",
                    ["type"] = "ss",
                    ["server"] = "node.example.com",
                    ["port"] = "invalid",
                    ["cipher"] = "aes-128-gcm",
                    ["password"] = "sensitive-password"
                })
            ]
        };

        Assert.Throws<NodeParseException>(() =>
            builder.Build(config, strictNodeValidation: true));

        Assert.That(logger.Messages, Is.Empty);
    }

    [TestCase("🚀 PROXIES")]
    [TestCase("🇺🇸 US AUTO")]
    [TestCase("dns-direct-alidns")]
    [TestCase("response-domestic-primary")]
    [TestCase("response-priority-primary-a")]
    [TestCase("response-priority-secondary-a")]
    [TestCase("response-domestic-primary-a")]
    [TestCase("response-domestic-secondary-a")]
    [TestCase("response-global-primary-a")]
    [TestCase("response-global-secondary-a")]
    [TestCase("cn")]
    public void StrictValidationRejectsReservedGeneratedTagNames(
        string reservedTag)
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder(
            [new ShadowsocksConverter()],
            logger, BoxForgeProfile.Default);
        var config = new ClashConfig
        {
            Proxies =
            [
                new ClashProxyNode(new Hashtable
                {
                    ["name"] = reservedTag,
                    ["type"] = "ss",
                    ["server"] = "node.example.com",
                    ["port"] = 443,
                    ["cipher"] = "aes-128-gcm",
                    ["password"] = "test-only"
                })
            ]
        };

        var exception = Assert.Throws<NodeParseException>(() =>
            builder.Build(config, strictNodeValidation: true));

        Assert.Multiple(() =>
        {
            Assert.That(
                exception!.Message,
                Is.EqualTo($"节点名称 '{reservedTag}' 与 BoxForge 保留 tag 冲突"));
            Assert.That(logger.Messages, Is.Empty);
        });
    }

    [TestCase("response-priority-primary-a")]
    [TestCase("response-priority-secondary-a")]
    [TestCase("response-domestic-primary-a")]
    [TestCase("response-domestic-secondary-a")]
    [TestCase("response-global-primary-a")]
    [TestCase("response-global-secondary-a")]
    public void NonStrictValidationWarnsAndSkipsReservedAddressResponseTags(string reservedTag)
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder([new ShadowsocksConverter()], logger, BoxForgeProfile.Default);
        var config = new ClashConfig
        {
            Proxies =
            [
                new ClashProxyNode(new Hashtable
                {
                    ["name"] = reservedTag,
                    ["type"] = "ss",
                    ["server"] = "node.example.com",
                    ["port"] = 443,
                    ["cipher"] = "aes-128-gcm",
                    ["password"] = "test-only"
                })
            ]
        };

        var catalog = builder.Build(config, strictNodeValidation: false);

        Assert.Multiple(() =>
        {
            Assert.That(catalog.Outbounds, Is.Empty);
            Assert.That(catalog.Names, Is.Empty);
            Assert.That(logger.Messages, Has.Count.EqualTo(1));
            Assert.That(logger.Messages.Single(), Does.Contain(reservedTag).And.Contain("保留 tag 冲突"));
            Assert.That(logger.Levels, Is.EqualTo(new[] { LogLevel.Warning }));
        });
    }

    [TestCase(null, false)]
    [TestCase("unsupported", false)]
    [TestCase(null, true)]
    [TestCase("unsupported", true)]
    public void MissingOrUnsupportedTypesWarnOnlyInNonStrictMode(string? type, bool strict)
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder([new ShadowsocksConverter()], logger, BoxForgeProfile.Default);
        var properties = new Hashtable { ["name"] = "invalid-node" };
        if (type != null)
        {
            properties["type"] = type;
        }
        var config = new ClashConfig { Proxies = [new ClashProxyNode(properties)] };

        if (strict)
        {
            Assert.Throws<NodeParseException>(() => builder.Build(config, strictNodeValidation: true));
            Assert.That(logger.Messages, Is.Empty);
        }
        else
        {
            var catalog = builder.Build(config, strictNodeValidation: false);
            Assert.Multiple(() =>
            {
                Assert.That(catalog.Outbounds, Is.Empty);
                Assert.That(catalog.Names, Is.Empty);
                Assert.That(logger.Messages, Has.Count.EqualTo(1));
                Assert.That(logger.Messages.Single(), Does.Contain("invalid-node")
                    .And.Contain(type == null ? "缺少 type" : "不支持的类型: unsupported"));
                Assert.That(logger.Levels, Is.EqualTo(new[] { LogLevel.Warning }));
            });
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Levels.Add(logLevel);
        }
    }
}
