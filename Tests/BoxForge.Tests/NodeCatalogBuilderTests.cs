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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

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
        }
    }
}
