using System.Collections;
using BoxForge.Builders.Components;
using BoxForge.Converters;
using BoxForge.Exceptions;
using BoxForge.Models.Clash;
using Microsoft.Extensions.Logging;

namespace BoxForge.Tests;

[TestFixture]
public sealed class NodeCatalogBuilderTests
{
    [Test]
    public void StrictValidationDoesNotLogInputDerivedNodeDetails()
    {
        var logger = new RecordingLogger<NodeCatalogBuilder>();
        var builder = new NodeCatalogBuilder(
            [new ShadowsocksConverter()],
            logger);
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
            logger);
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
        var builder = new NodeCatalogBuilder([new ShadowsocksConverter()], logger);
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
        var builder = new NodeCatalogBuilder([new ShadowsocksConverter()], logger);
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
