using BoxForge.Builders.Components;
using BoxForge.Converters;
using BoxForge.Models.Clash;
using BoxForge.Parsers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ClashDnsPolicyTests
{
    [Test]
    public void ParserAndCatalogPreserveMatchingNodeDnsPolicy()
    {
        const string yaml = """
            dns:
              nameserver-policy:
                '+.node.example':
                  - https://resolver.example:8443/dns-query
            proxies:
              - name: node
                type: ss
                server: edge.node.example
                port: 443
                cipher: aes-128-gcm
                password: secret
            """;
        ClashConfig config = new ClashParser().Parse(yaml)!;
        var builder = new NodeCatalogBuilder(
            [new ShadowsocksConverter()],
            NullLogger<NodeCatalogBuilder>.Instance);

        var catalog = builder.Build(config);

        Assert.That(
            catalog.Outbounds.Single().ProbeDnsServers,
            Is.EqualTo(new[] { "https://resolver.example:8443/dns-query" }));
    }

    [Test]
    public void PolicyDoesNotMatchUnrelatedSuffix()
    {
        var config = new ClashConfig
        {
            DnsPolicies =
            [
                new ClashDnsPolicy(
                    "+.node.example",
                    ["https://resolver.example/dns-query"])
            ]
        };

        Assert.That(config.FindNodeDnsServers("notnode.example"), Is.Empty);
    }
}
