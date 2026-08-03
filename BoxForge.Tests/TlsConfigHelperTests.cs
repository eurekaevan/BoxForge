using BoxForge.Helpers;
using BoxForge.Models.Clash;
using BoxForge.Models.Singbox;

namespace BoxForge.Tests;

public class TlsConfigHelperTests
{
    [Fact]
    public void Extract_OmitsUnspecifiedAlpnAndMinimumVersion()
    {
        var node = CreateNode(new Dictionary<string, object>
        {
            ["tls"] = true
        });

        var tls = Assert.IsType<OutboundTls>(
            TlsConfigHelper.Extract(node, "example.com"));

        Assert.Null(tls.Alpn);
        Assert.Null(tls.MinVersion);
    }

    [Fact]
    public void Extract_PreservesExplicitAlpnAndMinimumVersion()
    {
        var node = CreateNode(new Dictionary<string, object>
        {
            ["tls"] = true,
            ["alpn"] = new[] { "h2", "http/1.1" },
            ["min-version"] = "1.2"
        });

        var tls = Assert.IsType<OutboundTls>(
            TlsConfigHelper.Extract(node, "example.com"));

        Assert.Equal(["h2", "http/1.1"], tls.Alpn);
        Assert.Equal("1.2", tls.MinVersion);
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
