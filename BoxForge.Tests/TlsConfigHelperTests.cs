using System.Text.Json;
using BoxForge.Helpers;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;

namespace BoxForge.Tests;

public class TlsConfigHelperTests
{
    [Fact]
    public void Extract_IgnoresAlpnAndMinimumVersion()
    {
        var node = CreateNode(new Dictionary<string, object>
        {
            ["tls"] = true,
            ["alpn"] = new[] { "h2", "http/1.1" },
            ["min-version"] = "1.2"
        });

        var tls = Assert.IsType<OutboundTls>(
            TlsConfigHelper.Extract(node, "example.com"));

        string json = JsonSerializer.Serialize(tls);
        Assert.DoesNotContain("alpn", json);
        Assert.DoesNotContain("min_version", json);
    }

    [Fact]
    public void Extract_OmitsUtlsWhenProtocolDoesNotSupportIt()
    {
        var node = CreateNode(new Dictionary<string, object>
        {
            ["client-fingerprint"] = "chrome"
        });

        var tls = Assert.IsType<OutboundTls>(
            TlsConfigHelper.Extract(
                node,
                "example.com",
                forceTls: true,
                supportsUtls: false));

        Assert.Null(tls.Utls);
    }

    private static ClashProxyNode CreateNode(Dictionary<string, object> values) =>
        new(values);
}
