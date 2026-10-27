using System.Text.Json;
using BoxForge.Models.Singbox;
using BoxForge.Services;

namespace BoxForge.Tests;

[TestFixture]
public sealed class ConfigSerializerDefaultsTests
{
    [TestCase(null)]
    [TestCase(false)]
    [TestCase(true)]
    public void DnsServersSerializeAndRoundTripThroughTheirPolymorphicBase(bool? acceptDefaultResolvers)
    {
        var config = new SingboxConfig
        {
            Dns = new DnsConfig
            {
                Servers =
                [
                    new HttpsDnsServer
                    {
                        Tag = "https", Server = "1.1.1.1", Detour = "proxy",
                        Tls = new DnsTlsConfig { Enabled = true, ServerName = "cloudflare-dns.com" }
                    },
                    new TailscaleDnsServer
                    {
                        Tag = "tailscale", Endpoint = "endpoint", AcceptDefaultResolvers = acceptDefaultResolvers
                    }
                ]
            }
        };
        using var document = JsonDocument.Parse(new ConfigSerializer().Serialize(config));
        JsonElement dns = document.RootElement.GetProperty("dns");
        JsonElement servers = dns.GetProperty("servers");
        var restored = JsonSerializer.Deserialize<DnsConfig>(dns.GetRawText())!;
        Assert.Multiple(() =>
        {
            Assert.That(servers[0].GetProperty("type").GetString(), Is.EqualTo("https"));
            Assert.That(servers[0].EnumerateObject().Select(property => property.Name),
                Is.EqualTo(new[] { "type", "tag", "server", "detour", "tls" }));
            Assert.That(servers[1].GetProperty("type").GetString(), Is.EqualTo("tailscale"));
            Assert.That(servers[1].EnumerateObject().Select(property => property.Name),
                Is.EqualTo(acceptDefaultResolvers is null ? new[] { "type", "tag", "endpoint" }
                    : new[] { "type", "tag", "endpoint", "accept_default_resolvers" }));
            Assert.That(restored.Servers[0], Is.EqualTo(config.Dns.Servers[0]));
            Assert.That(restored.Servers[1], Is.EqualTo(config.Dns.Servers[1]));
            if (acceptDefaultResolvers is { } expected)
                Assert.That(servers[1].GetProperty("accept_default_resolvers").GetBoolean(), Is.EqualTo(expected));
        });
    }

    [Test]
    public void TargetModelsDoNotInventProductPolicyWhenFieldsAreOmitted()
    {
        var config = new SingboxConfig
        {
            Dns = new DnsConfig
            {
                Optimistic = new DnsOptimisticConfig(),
                Servers = [new HttpsDnsServer
                {
                    Tag = "resolver", Server = "1.1.1.1",
                    Tls = new DnsTlsConfig { ServerName = "cloudflare-dns.com" }
                }]
            },
            Outbounds = [new TrojanOutbound
            {
                Tag = "node", Server = "node.example.com", Password = "test-only",
                Tls = new OutboundTls { ServerName = "node.example.com" }
            }]
        };
        using var document = JsonDocument.Parse(new ConfigSerializer().Serialize(config));
        JsonElement root = document.RootElement;
        JsonElement dns = root.GetProperty("dns");
        Assert.Multiple(() =>
        {
            foreach (string field in new[] { "timeout", "cache_capacity", "final", "reverse_mapping" })
                Assert.That(dns.TryGetProperty(field, out _), Is.False, field);
            Assert.That(dns.GetProperty("optimistic").EnumerateObject(), Is.Empty);
            Assert.That(root.GetProperty("log").EnumerateObject(), Is.Empty);
            Assert.That(root.GetProperty("route").EnumerateObject(), Is.Empty);
            Assert.That(dns.GetProperty("servers")[0].GetProperty("tls").TryGetProperty("enabled", out _), Is.False);
            Assert.That(root.GetProperty("outbounds")[0].GetProperty("tls").TryGetProperty("enabled", out _), Is.False);
        });
    }

    [Test]
    public void ExplicitFalseAndZeroPolicyValuesSurviveSerialization()
    {
        var config = new SingboxConfig
        {
            Log = new LogConfig { Level = "error", Timestamp = false },
            Dns = new DnsConfig
            {
                Timeout = "2s",
                Final = "resolver",
                CacheCapacity = 0,
                ReverseMapping = false,
                Optimistic = new DnsOptimisticConfig { Enabled = false, Timeout = "0s" }
            },
            Route = new RouteConfig { AutoDetectInterface = false },
            Outbounds = [new TrojanOutbound
            {
                Tag = "node", Server = "node.example.com", Password = "test-only",
                Tls = new OutboundTls { Enabled = false, ServerName = "node.example.com" }
            }]
        };
        using var document = JsonDocument.Parse(new ConfigSerializer().Serialize(config));
        JsonElement root = document.RootElement;
        JsonElement dns = root.GetProperty("dns");
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("log").GetProperty("timestamp").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("route").GetProperty("auto_detect_interface").GetBoolean(), Is.False);
            Assert.That(dns.GetProperty("cache_capacity").GetUInt32(), Is.Zero);
            Assert.That(dns.GetProperty("reverse_mapping").GetBoolean(), Is.False);
            Assert.That(dns.GetProperty("optimistic").GetProperty("enabled").GetBoolean(), Is.False);
            Assert.That(dns.GetProperty("optimistic").GetProperty("timeout").GetString(), Is.EqualTo("0s"));
            Assert.That(root.GetProperty("outbounds")[0].GetProperty("tls").GetProperty("enabled").GetBoolean(), Is.False);
        });
    }

    [Test]
    public void PreservesExplicitDefaultsWithoutChangingTheModel()
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
            Tls = new OutboundTls { Enabled = true, ServerName = "example.com", Insecure = false }
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
        Assert.That(node.GetProperty("connect_timeout").GetString(), Is.EqualTo("5000ms"));
        Assert.That(node.GetProperty("tcp_keep_alive").GetString(), Is.EqualTo("5m"));
        Assert.That(node.GetProperty("tcp_keep_alive_interval").GetString(), Is.EqualTo("1m15s"));
        Assert.That(node.GetProperty("idle_session_timeout").GetString(), Is.EqualTo("0.5m"));
        Assert.That(node.GetProperty("idle_session_check_interval").GetString(), Is.EqualTo("30s"));
        Assert.That(node.GetProperty("min_idle_session").GetInt32(), Is.Zero);
        Assert.That(node.GetProperty("client_metadata").GetString(), Is.Empty);
        Assert.That(node.GetProperty("tls").GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(node.GetProperty("tls").GetProperty("insecure").GetBoolean(), Is.False);
        Assert.That(document.RootElement.GetProperty("outbounds")[1].GetProperty("default").GetString(), Is.EqualTo("node"));
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

    [TestCase(1)]
    [TestCase(2)]
    public void PreservesBothUotVersions(int version)
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
        Assert.That(uot.GetProperty("version").GetInt32(), Is.EqualTo(version));
    }

    [Test]
    public void PreservesExplicitDnsRouteAndRuleSetSettings()
    {
        var config = new SingboxConfig
        {
            Dns = new DnsConfig
            {
                Timeout = "1s",
                CacheCapacity = 4096,
                Rules = [new DnsRule { Action = DnsRuleAction.Predefined, Rcode = DnsResponseCode.NoError },
                    new DnsRule { Action = DnsRuleAction.Respond, ResponseRcode = DnsResponseCode.NoError }]
            },
            Route = new RouteConfig
            {
                AutoDetectInterface = true,
                RuleSet = [new SingboxRuleSet { Format = RuleSetFormat.Binary, Url = "https://example.com/rules.srs", UpdateInterval = "24h" },
                    new SingboxRuleSet { Format = RuleSetFormat.Binary, Url = "https://example.com/download" }],
                Rules = [new RouteRule { Action = RouteRuleAction.Route, Outbound = "direct", Invert = false },
                    new RouteRule { Action = RouteRuleAction.Sniff, Timeout = "300ms" }]
            }
        };
        using JsonDocument document = JsonDocument.Parse(new ConfigSerializer().Serialize(config));
        JsonElement dns = document.RootElement.GetProperty("dns");
        Assert.That(dns.GetProperty("timeout").GetString(), Is.EqualTo("1s"));
        Assert.That(dns.GetProperty("cache_capacity").GetUInt32(), Is.EqualTo(4096));
        Assert.That(dns.GetProperty("rules")[0].GetProperty("rcode").GetString(), Is.EqualTo("NOERROR"));
        Assert.That(dns.GetProperty("rules")[1].GetProperty("response_rcode").GetString(), Is.EqualTo("NOERROR"));
        JsonElement route = document.RootElement.GetProperty("route");
        Assert.That(route.GetProperty("rule_set")[0].GetProperty("format").GetString(), Is.EqualTo("binary"));
        Assert.That(route.GetProperty("rule_set")[0].GetProperty("update_interval").GetString(), Is.EqualTo("24h"));
        Assert.That(route.GetProperty("rule_set")[1].GetProperty("format").GetString(), Is.EqualTo("binary"));
        Assert.That(route.GetProperty("rules")[0].GetProperty("action").GetString(), Is.EqualTo("route"));
        Assert.That(route.GetProperty("rules")[0].GetProperty("invert").GetBoolean(), Is.False);
        Assert.That(route.GetProperty("rules")[1].GetProperty("timeout").GetString(), Is.EqualTo("300ms"));
        Assert.That(route.GetProperty("auto_detect_interface").GetBoolean(), Is.True);
    }

    [TestCase(null)]
    [TestCase("0s")]
    [TestCase("30s")]
    [TestCase("60s")]
    public void PreservesHysteriaHopBoundsWithoutNormalizingDurations(string? maximum)
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
        Assert.That(node.GetProperty("hop_interval").GetString(), Is.EqualTo("30s"));
        if (maximum is null)
            Assert.That(node.TryGetProperty("hop_interval_max", out _), Is.False);
        else
            Assert.That(node.GetProperty("hop_interval_max").GetString(), Is.EqualTo(maximum));
        Assert.That(node.GetProperty("bbr_profile").GetString(), Is.EqualTo("standard"));
    }

    [Test]
    public void PreservesUrlTestAndTlsDefaultsAndOmitsNullsAndEmptyCollections()
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
            Tls = new OutboundTls { Enabled = true, ServerName = "example.com", Alpn = [], Utls = new Utls { Enabled = true, Fingerprint = "chrome" } }
        });
        JsonElement auto = document.RootElement.GetProperty("outbounds")[0];
        Assert.That(auto.GetProperty("url").GetString(), Is.EqualTo("https://www.gstatic.com/generate_204"));
        Assert.That(auto.GetProperty("interval").GetString(), Is.EqualTo("3m"));
        Assert.That(auto.GetProperty("idle_timeout").GetString(), Is.EqualTo("30m"));
        Assert.That(auto.GetProperty("tolerance").GetInt32(), Is.EqualTo(50));
        Assert.That(auto.GetProperty("interrupt_exist_connections").GetBoolean(), Is.False);
        JsonElement node = document.RootElement.GetProperty("outbounds")[1];
        Assert.That(node.GetProperty("packet_encoding").GetString(), Is.EqualTo("xudp"));
        Assert.That(node.GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("example.com"));
        Assert.That(node.GetProperty("tls").GetProperty("utls").GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(node.GetProperty("tls").GetProperty("utls").GetProperty("fingerprint").GetString(), Is.EqualTo("chrome"));
        Assert.That(node.GetProperty("tls").TryGetProperty("alpn", out _), Is.False);
        Assert.That(node.GetProperty("tls").TryGetProperty("reality", out _), Is.False);
        Assert.That(document.RootElement.TryGetProperty("inbounds", out _), Is.False);
        Assert.That(document.RootElement.TryGetProperty("services", out _), Is.False);
    }

    [Test]
    public void PreservesExplicitReferencesTimeoutsAndDnsTlsSettings()
    {
        var config = new SingboxConfig
        {
            Outbounds = [new DirectOutbound { Tag = "direct" }],
            HttpClients = [new HttpClientConfig { Tag = "first", Detour = "direct" }],
            Dns = new DnsConfig
            {
                Servers = [new HttpsDnsServer { Tag = "dns", Server = "1.1.1.1", Tls = new DnsTlsConfig { Enabled = true, ServerName = "cloudflare-dns.com" } }],
                Final = "dns",
                Timeout = "2s",
                Rules = [new DnsRule { Timeout = "2000ms", Server = "dns" }, new DnsRule { Timeout = "10s", Server = "dns" }]
            },
            Route = new RouteConfig
            {
                Final = "direct",
                DefaultHttpClient = "first",
                RuleSet = [new SingboxRuleSet { HttpClient = "first" }]
            },
            Inbounds = [new Inbound { Type = "tun", Tag = "tun", DnsMode = "hijack" }]
        };
        using JsonDocument document = JsonDocument.Parse(new ConfigSerializer().Serialize(config));
        JsonElement dns = document.RootElement.GetProperty("dns");
        Assert.That(dns.GetProperty("final").GetString(), Is.EqualTo("dns"));
        Assert.That(dns.GetProperty("rules")[0].GetProperty("timeout").GetString(), Is.EqualTo("2000ms"));
        Assert.That(dns.GetProperty("rules")[1].GetProperty("timeout").GetString(), Is.EqualTo("10s"));
        Assert.That(dns.GetProperty("servers")[0].GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("cloudflare-dns.com"));
        Assert.That(dns.GetProperty("servers")[0].GetProperty("tls").GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(document.RootElement.GetProperty("route").GetProperty("default_http_client").GetString(), Is.EqualTo("first"));
        Assert.That(document.RootElement.GetProperty("route").GetProperty("final").GetString(), Is.EqualTo("direct"));
        Assert.That(document.RootElement.GetProperty("route").GetProperty("rule_set")[0].GetProperty("http_client").GetString(), Is.EqualTo("first"));
        Assert.That(document.RootElement.GetProperty("inbounds")[0].GetProperty("dns_mode").GetString(), Is.EqualTo("hijack"));
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
    public void PreservesTlsIdentityAndAlpnOrder()
    {
        var tls = new OutboundTls { Enabled = true, ServerName = "name.example.com", Alpn = ["http/1.1"] };
        using JsonDocument document = Serialize(new TrojanOutbound
        {
            Tag = "default",
            Server = "node.example.com",
            Password = "test",
            Tls = tls
        }, new TrojanOutbound
        {
            Tag = "custom",
            Server = "node.example.com",
            Password = "test",
            Tls = tls with { Alpn = ["h2", "http/1.1", "h2"] }
        });
        JsonElement first = document.RootElement.GetProperty("outbounds")[0];
        Assert.That(first.GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("name.example.com"));
        Assert.That(first.GetProperty("tls").GetProperty("alpn").EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { "http/1.1" }));
        JsonElement second = document.RootElement.GetProperty("outbounds")[1];
        Assert.That(second.GetProperty("tls").GetProperty("alpn").EnumerateArray().Select(item => item.GetString()),
            Is.EqualTo(new[] { "h2", "http/1.1", "h2" }));
        Assert.That(tls.Alpn, Is.EqualTo(new[] { "http/1.1" }));
    }

    [Test]
    public void ConcurrentSerializationKeepsIndependentContextAndSharedModelsIntact()
    {
        var serializer = new ConfigSerializer();
        var tls = new OutboundTls { Enabled = true, ServerName = "name.example.com" };
        Parallel.For(0, 32, index =>
        {
            string server = index % 2 == 0 ? "name.example.com" : "other.example.com";
            var config = new SingboxConfig
            {
                Outbounds = [new AnyTlsOutbound { Tag = "node", Server = server, Password = "test", Tls = tls }]
            };
            using JsonDocument document = JsonDocument.Parse(serializer.Serialize(config));
            Assert.That(document.RootElement.GetProperty("outbounds")[0].GetProperty("tls").GetProperty("server_name").GetString(),
                Is.EqualTo("name.example.com"));
        });
        Assert.That(tls.ServerName, Is.EqualTo("name.example.com"));
    }

    private static JsonDocument Serialize(params Outbound[] outbounds) =>
        JsonDocument.Parse(new ConfigSerializer().Serialize(new SingboxConfig { Outbounds = [.. outbounds] }));
}
