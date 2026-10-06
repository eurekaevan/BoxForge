using System.Text.Json;
using BoxForge.Models.Singbox;
using BoxForge.Services;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ConfigSerializerDefaultsTests
{
    [Test]
    public void OmitsCoreDefaultsWithoutChangingTheModel()
    {
        var leaf = new AnyTlsOutbound
        {
            Tag = "node",
            Server = "example.com",
            ServerPort = 443,
            Password = "test",
            ConnectTimeout = "5000ms",
            TcpKeepAlive = "5m",
            TcpKeepAliveInterval = "1m15s",
            IdleSessionTimeout = "0.5m",
            IdleSessionCheckInterval = "30s",
            MinIdleSession = 0,
            ClientMetadata = "",
            Tls = new OutboundTls { ServerName = "example.com", Insecure = false }
        };
        var selector = new SelectorOutbound
        {
            Tag = "group",
            Outbounds = ["node"],
            Default = "node",
            InterruptExistConnections = true
        };
        using JsonDocument document = Serialize(leaf, selector);
        JsonElement node = document.RootElement.GetProperty("outbounds")[0];
        foreach (string field in new[] { "connect_timeout", "tcp_keep_alive", "tcp_keep_alive_interval",
                     "idle_session_timeout", "idle_session_check_interval", "min_idle_session", "client_metadata" })
            Assert.That(node.TryGetProperty(field, out _), Is.False, field);
        Assert.That(node.GetProperty("tls").GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(node.GetProperty("tls").TryGetProperty("insecure", out _), Is.False);
        Assert.That(document.RootElement.GetProperty("outbounds")[1].TryGetProperty("default", out _), Is.False);
        Assert.That(document.RootElement.GetProperty("outbounds")[1].GetProperty("interrupt_exist_connections").GetBoolean(), Is.True);
        Assert.That(leaf.IdleSessionTimeout, Is.EqualTo("0.5m"));
        Assert.That(selector.Default, Is.EqualTo("node"));
    }

    [Test]
    public void PreservesNonDefaultsAndExplicitNativeVlessEncoding()
    {
        using JsonDocument document = Serialize(new VlessOutbound
        {
            Tag = "node",
            Server = "example.com",
            ServerPort = 443,
            Uuid = "test",
            PacketEncoding = "",
            ConnectTimeout = "2s",
            TcpKeepAlive = "1m",
            TcpKeepAliveInterval = "30s"
        }, new SelectorOutbound { Tag = "group", Outbounds = ["node", "other"], Default = "other" });
        JsonElement node = document.RootElement.GetProperty("outbounds")[0];
        Assert.That(node.GetProperty("packet_encoding").GetString(), Is.Empty);
        Assert.That(node.GetProperty("connect_timeout").GetString(), Is.EqualTo("2s"));
        Assert.That(node.GetProperty("tcp_keep_alive").GetString(), Is.EqualTo("1m"));
        Assert.That(node.GetProperty("tcp_keep_alive_interval").GetString(), Is.EqualTo("30s"));
        Assert.That(document.RootElement.GetProperty("outbounds")[1].GetProperty("default").GetString(), Is.EqualTo("other"));
    }

    [TestCase(1, true)]
    [TestCase(2, false)]
    public void UotVersionOneRemainsExplicitWhileVersionTwoUsesTheCoreDefault(int version, bool explicitVersion)
    {
        using JsonDocument document = Serialize(new ShadowsocksOutbound
        {
            Tag = "ss",
            Server = "example.com",
            Method = "none",
            Password = "",
            UdpOverTcp = new UdpOverTcpOptions { Enabled = true, Version = version }
        });
        JsonElement uot = document.RootElement.GetProperty("outbounds")[0].GetProperty("udp_over_tcp");
        Assert.That(uot.GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(uot.TryGetProperty("version", out _), Is.EqualTo(explicitVersion));
    }

    [Test]
    public void DoesNotRemoveResponseMatchersOrNonDefaultPolicies()
    {
        var config = new SingboxConfig
        {
            Dns = new DnsConfig
            {
                Rules = [new DnsRule { Action = DnsRuleAction.Predefined, Rcode = DnsResponseCode.NoError },
                    new DnsRule { Action = DnsRuleAction.Respond, ResponseRcode = DnsResponseCode.NoError }]
            },
            Route = new RouteConfig
            {
                RuleSet = [new SingboxRuleSet { Format = RuleSetFormat.Binary, Url = "https://example.com/rules.srs", UpdateInterval = "24h" },
                    new SingboxRuleSet { Format = RuleSetFormat.Binary, Url = "https://example.com/download" }],
                Rules = [new RouteRule { Action = RouteRuleAction.Route, Outbound = "direct", Invert = false }]
            }
        };
        using JsonDocument document = JsonDocument.Parse(new ConfigSerializer().Serialize(config));
        JsonElement dns = document.RootElement.GetProperty("dns");
        Assert.That(dns.GetProperty("timeout").GetString(), Is.EqualTo("5s"));
        Assert.That(dns.GetProperty("cache_capacity").GetUInt32(), Is.EqualTo(4096));
        Assert.That(dns.GetProperty("rules")[0].TryGetProperty("rcode", out _), Is.False);
        Assert.That(dns.GetProperty("rules")[1].GetProperty("response_rcode").GetString(), Is.EqualTo("NOERROR"));
        JsonElement route = document.RootElement.GetProperty("route");
        Assert.That(route.GetProperty("rule_set")[0].TryGetProperty("format", out _), Is.False);
        Assert.That(route.GetProperty("rule_set")[0].TryGetProperty("update_interval", out _), Is.False);
        Assert.That(route.GetProperty("rule_set")[1].GetProperty("format").GetString(), Is.EqualTo("binary"));
        Assert.That(route.GetProperty("rules")[0].TryGetProperty("action", out _), Is.False);
        Assert.That(route.GetProperty("auto_detect_interface").GetBoolean(), Is.True);
    }

    [TestCase(null, false)]
    [TestCase("0s", false)]
    [TestCase("30s", false)]
    [TestCase("60s", true)]
    public void HysteriaHopDefaultCanOnlyBeOmittedForAFixedInterval(string? maximum, bool explicitMinimum)
    {
        using JsonDocument document = Serialize(new Hysteria2Outbound
        {
            Tag = "hy2",
            Server = "example.com",
            Password = "test",
            HopInterval = "30s",
            HopIntervalMax = maximum,
            BbrProfile = "standard"
        });
        JsonElement node = document.RootElement.GetProperty("outbounds")[0];
        Assert.That(node.TryGetProperty("hop_interval", out _), Is.EqualTo(explicitMinimum));
        Assert.That(node.TryGetProperty("hop_interval_max", out _), Is.EqualTo(explicitMinimum));
        Assert.That(node.TryGetProperty("bbr_profile", out _), Is.False);
    }

    [Test]
    public void OmitsUrlTestAndTransportDefaultsButPreservesRequiredEnablers()
    {
        using JsonDocument document = Serialize(new UrlTestOutbound
        {
            Tag = "auto",
            Outbounds = ["node", "other"],
            Url = "https://www.gstatic.com/generate_204",
            Interval = "3m",
            IdleTimeout = "30m",
            Tolerance = 50,
            InterruptExistConnections = false
        }, new VlessOutbound
        {
            Tag = "node",
            Server = "example.com",
            Uuid = "test",
            PacketEncoding = "xudp",
            Tls = new OutboundTls { ServerName = "example.com", Utls = new Utls { Enabled = true, Fingerprint = "chrome" } },
            Transport = new WebSocketTransport { Path = "/", MaxEarlyData = 0, Headers = [] }
        });
        JsonElement auto = document.RootElement.GetProperty("outbounds")[0];
        Assert.That(auto.EnumerateObject().Select(property => property.Name), Is.EquivalentTo(new[] { "type", "tag", "outbounds" }));
        JsonElement node = document.RootElement.GetProperty("outbounds")[1];
        Assert.That(node.TryGetProperty("packet_encoding", out _), Is.False);
        Assert.That(node.GetProperty("tls").TryGetProperty("server_name", out _), Is.False);
        Assert.That(node.GetProperty("tls").GetProperty("utls").GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(node.GetProperty("tls").GetProperty("utls").TryGetProperty("fingerprint", out _), Is.False);
        Assert.That(node.GetProperty("transport").EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "type" }));
    }

    [Test]
    public void ContextDefaultsTrackTheirActualParentsAndDoNotMutateThem()
    {
        var config = new SingboxConfig
        {
            Outbounds = [new DirectOutbound { Tag = "direct" }],
            HttpClients = [new HttpClientConfig { Tag = "first", Detour = "direct" }],
            Dns = new DnsConfig
            {
                Servers = [new HttpsDnsServer { Tag = "dns", ServerAddress = "1.1.1.1", TlsConfig = new DnsTlsConfig { ServerName = "cloudflare-dns.com" } }],
                Final = "dns",
                Timeout = "2s",
                Rules = [new DnsRule { Timeout = "2000ms", Server = "dns" }, new DnsRule { Timeout = "10s", Server = "dns" }]
            },
            Route = new RouteConfig { Final = "direct", DefaultHttpClient = "first" },
            Inbounds = [new Inbound { Type = "tun", Tag = "tun", DnsMode = "hijack" }]
        };
        using JsonDocument document = JsonDocument.Parse(new ConfigSerializer().Serialize(config));
        JsonElement dns = document.RootElement.GetProperty("dns");
        Assert.That(dns.TryGetProperty("final", out _), Is.False);
        Assert.That(dns.GetProperty("rules")[0].TryGetProperty("timeout", out _), Is.False);
        Assert.That(dns.GetProperty("rules")[1].GetProperty("timeout").GetString(), Is.EqualTo("10s"));
        Assert.That(dns.GetProperty("servers")[0].GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("cloudflare-dns.com"));
        Assert.That(document.RootElement.GetProperty("route").TryGetProperty("default_http_client", out _), Is.False);
        Assert.That(document.RootElement.GetProperty("route").TryGetProperty("final", out _), Is.False);
        Assert.That(document.RootElement.GetProperty("inbounds")[0].TryGetProperty("dns_mode", out _), Is.False);
        Assert.That(config.Dns.Rules[0].Timeout, Is.EqualTo("2000ms"));
        Assert.That(config.Route.DefaultHttpClient, Is.EqualTo("first"));
    }

    [Test]
    public void NeverMakesInvalidDurationsLookValidByOmittingThem()
    {
        using JsonDocument document = Serialize(new AnyTlsOutbound
        {
            Tag = "node",
            Server = "example.com",
            Password = "test",
            IdleSessionTimeout = "+"
        });
        Assert.That(document.RootElement.GetProperty("outbounds")[0].GetProperty("idle_session_timeout").GetString(), Is.EqualTo("+"));
    }

    [Test]
    public void TransportDefaultsUseTheOriginalTlsIdentityAndKeepNonDefaultAlpn()
    {
        var tls = new OutboundTls { ServerName = "name.example.com", Alpn = ["http/1.1"] };
        using JsonDocument document = Serialize(new TrojanOutbound
        {
            Tag = "default",
            Server = "node.example.com",
            Password = "test",
            Tls = tls,
            Transport = new HttpUpgradeTransport { Host = "name.example.com", Path = "/" }
        }, new TrojanOutbound
        {
            Tag = "custom",
            Server = "node.example.com",
            Password = "test",
            Tls = tls with { Alpn = ["h2", "http/1.1", "h2"] },
            Transport = new HttpUpgradeTransport { Host = "other.example.com", Path = "/custom" }
        });
        JsonElement first = document.RootElement.GetProperty("outbounds")[0];
        Assert.That(first.GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("name.example.com"));
        Assert.That(first.GetProperty("tls").TryGetProperty("alpn", out _), Is.False);
        Assert.That(first.GetProperty("transport").TryGetProperty("host", out _), Is.False);
        JsonElement second = document.RootElement.GetProperty("outbounds")[1];
        Assert.That(second.GetProperty("transport").GetProperty("host").GetString(), Is.EqualTo("other.example.com"));
        Assert.That(second.GetProperty("tls").GetProperty("alpn").EnumerateArray().Select(item => item.GetString()),
            Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
        Assert.That(tls.Alpn, Is.EqualTo(new[] { "http/1.1" }));
    }

    [Test]
    public void ConcurrentSerializationKeepsIndependentContextAndSharedModelsIntact()
    {
        var serializer = new ConfigSerializer();
        var tls = new OutboundTls { ServerName = "name.example.com" };
        Parallel.For(0, 32, index =>
        {
            string server = index % 2 == 0 ? "name.example.com" : "other.example.com";
            var config = new SingboxConfig
            {
                Outbounds = [new AnyTlsOutbound { Tag = "node", Server = server, Password = "test", Tls = tls }]
            };
            using JsonDocument document = JsonDocument.Parse(serializer.Serialize(config));
            Assert.That(document.RootElement.GetProperty("outbounds")[0].GetProperty("tls").TryGetProperty("server_name", out _),
                Is.EqualTo(index % 2 != 0));
        });
        Assert.That(tls.ServerName, Is.EqualTo("name.example.com"));
    }

    private static JsonDocument Serialize(params Outbound[] outbounds) =>
        JsonDocument.Parse(new ConfigSerializer().Serialize(new SingboxConfig { Outbounds = [.. outbounds] }));
}
