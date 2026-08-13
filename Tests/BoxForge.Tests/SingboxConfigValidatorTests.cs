using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SingboxConfigValidatorTests
{
    private readonly SingboxConfigValidator validator = new();

    [Test]
    public void ValidConfigurationPasses()
    {
        Assert.DoesNotThrow(() => validator.Validate(CreateValidConfig()));
    }

    [Test]
    public void RuleSetHttpClientMustExist()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxRuleSet ruleSet = valid.Route.RuleSet[0] with
        {
            HttpClient = "missing-http"
        };
        SingboxConfig config = valid with
        {
            Route = valid.Route with
            {
                RuleSet = [ruleSet]
            }
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB016",
                "route.rule_set[0].http_client",
                "引用了不存在的 HTTP client。"));
    }

    [Test]
    public void HttpClientTagsMustBePresentAndUnique()
    {
        SingboxConfig config = CreateValidConfig() with
        {
            HttpClients =
            [
                new HttpClientConfig { Tag = "", Detour = "direct" },
                new HttpClientConfig { Tag = "http", Detour = "direct" },
                new HttpClientConfig { Tag = "http", Detour = "selector" }
            ]
        };

        AssertDiagnostics(
            config,
            new("SB008", "http_clients[0].tag", "标签不能为空。"),
            new("SB009", "http_clients[2].tag", "标签 'http' 重复。"));
    }

    [Test]
    public void HttpClientDetourMustExist()
    {
        SingboxConfig config = CreateValidConfig() with
        {
            HttpClients =
            [
                new HttpClientConfig
                {
                    Tag = "http",
                    Detour = "missing-target"
                }
            ]
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB015",
                "http_clients[0].detour",
                "引用了不存在的 outbound 或 endpoint。"));
    }

    [Test]
    public void ModuleDiagnosticsPreserveCodesPathsMessagesAndOrder()
    {
        SingboxConfig config = CreateValidConfig() with
        {
            HttpClients =
            [
                new HttpClientConfig
                {
                    Tag = "broken-http",
                    Detour = "missing-target"
                }
            ],
            Outbounds =
            [
                new VlessOutbound
                {
                    Tag = "proxy",
                    Server = "",
                    ServerPort = 0,
                    DomainResolver = "missing-dns",
                    Uuid = "",
                    Tls = new OutboundTls
                    {
                        ServerName = ""
                    }
                }
            ],
            Inbounds =
            [
                new Inbound
                {
                    Type = "mixed",
                    Tag = "tun",
                    ListenPort = 70000
                }
            ],
            Dns = CreateValidConfig().Dns with
            {
                Rules =
                [
                    new DnsRule
                    {
                        Server = "missing-dns"
                    },
                    new DnsRule
                    {
                        Action = DnsRuleAction.Evaluate,
                        Server = "dns"
                    },
                    new DnsRule
                    {
                        Action = DnsRuleAction.Predefined,
                        Server = "dns"
                    },
                    new DnsRule
                    {
                        Action = DnsRuleAction.Route,
                        Server = "dns",
                        Race = true
                    }
                ]
            },
            Route = new RouteConfig
            {
                Final = "missing-target",
                DefaultHttpClient = "missing-http",
                RuleSet =
                [
                    new SingboxRuleSet
                    {
                        Tag = "broken-rule-set",
                        Type = RuleSetType.Remote,
                        Url = ""
                    }
                ],
                Rules =
                [
                    new RouteRule
                    {
                        Outbound = "missing-route-target",
                        RuleSet = ["missing-rule-set"],
                        Inbound = ["missing-inbound"]
                    }
                ]
            },
            Experimental = new ExperimentalConfig
            {
                CacheFile = new CacheFileConfig
                {
                    Enabled = true,
                    Path = "",
                    CacheId = "short"
                }
            }
        };

        var exception = Assert.Throws<ConfigValidationException>(
            () => validator.Validate(config));

        Assert.That(exception!.Diagnostics, Is.EqualTo(new ConfigDiagnostic[]
        {
            new("SB001", "route.final", "不存在对应的 outbound 或 endpoint。"),
            new("SB014", "route.default_http_client", "引用了不存在的 HTTP client。"),
            new("SB015", "http_clients[0].detour", "引用了不存在的 outbound 或 endpoint。"),
            new("SB019", "outbounds[0].server", "代理服务器地址不能为空。"),
            new("SB020", "outbounds[0].server_port", "代理节点必须配置有效端口。"),
            new("SB004", "outbounds[0].domain_resolver", "引用了不存在的 DNS server。"),
            new("SB044", "outbounds[0].tls.server_name", "TLS server_name 不能为空。"),
            new("SB045", "outbounds[0].uuid", "VLESS UUID 不能为空。"),
            new("SB022", "inbounds[0].listen_port", "inbound 监听端口必须在 1-65535 之间。"),
            new("SB025", "dns.rules[0].action", "DNS 规则动作不能为空。"),
            new("SB007", "dns.rules[0].server", "引用了不存在的 DNS server。"),
            new("SB027", "dns.rules[1].tag", "evaluate 响应标签不能为空。"),
            new("SB042", "dns.rules[2].rcode", "predefined 动作必须指定 rcode。"),
            new("SB031", "dns.rules[3].race", "race 只允许用于 respond 动作。"),
            new("SB056", "route.rule_set[0].format", "rule-set format 不能为空。"),
            new("SB057", "route.rule_set[0].url", "远程 rule-set URL 不能为空。"),
            new("SB058", "experimental.cache_file.path", "缓存文件路径不能为空。"),
            new("SB059", "experimental.cache_file.cache_id", "cache_id 必须是完整的 64 位 SHA-256 十六进制字符串。"),
            new("SB010", "route.rules[0].outbound", "引用了不存在的 outbound 或 endpoint。"),
            new("SB011", "route.rules[0].rule_set[0]", "引用了不存在的 rule-set。"),
            new("SB034", "route.rules[0].inbound[0]", "引用了不存在的 inbound。"),
            new("SB035", "route.rules[0].action", "顶层路由规则必须指定 action。")
        }));
    }

    [Test]
    public void SelectorDiagnosticsPreserveOrder()
    {
        SingboxConfig config = CreateValidConfig() with
        {
            Outbounds =
            [
                CreateDirectOutbound(),
                new SelectorOutbound
                {
                    Tag = "selector",
                    Outbounds = ["missing", "direct", "direct"],
                    Default = "other"
                }
            ]
        };

        AssertDiagnostics(
            config,
            new("SB003", "outbounds[1].outbounds[0]", "selector 引用了不存在的目标。"),
            new("SB017", "outbounds[1].outbounds", "selector 不能包含重复目标。"),
            new("SB018", "outbounds[1].default", "selector 默认目标不在 outbounds 中。"));
    }

    [Test]
    public void DnsServerDiagnosticsPreserveOrder()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Dns = valid.Dns with
            {
                Servers =
                [
                    .. valid.Dns.Servers,
                    new HttpsDnsServer
                    {
                        Tag = "broken-https",
                        ServerAddress = "1.0.0.1",
                        DetourTag = "missing-target",
                        TlsConfig = new DnsTlsConfig { ServerName = "" }
                    },
                    new TailscaleDnsServer
                    {
                        Tag = "tailscale-dns",
                        EndpointTag = "missing-endpoint"
                    }
                ]
            }
        };

        AssertDiagnostics(
            config,
            new("SB005", "dns.servers[1].detour", "引用了不存在的 outbound 或 endpoint。"),
            new("SB054", "dns.servers[1].tls.server_name", "HTTPS DNS TLS server_name 不能为空。"),
            new("SB006", "dns.servers[2].endpoint", "引用了不存在的 Tailscale endpoint。"));
    }

    [Test]
    public void NestedRouteRuleReferencesUseRecursivePaths()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Route = valid.Route with
            {
                Rules =
                [
                    new RouteRule
                    {
                        Type = RouteRuleType.Logical,
                        Mode = RouteLogicalMode.And,
                        Action = RouteRuleAction.Reject,
                        Rules =
                        [
                            new RouteRule
                            {
                                Outbound = "missing-target",
                                RuleSet = ["missing-rule-set"],
                                Inbound = ["missing-inbound"]
                            }
                        ]
                    }
                ]
            }
        };

        AssertDiagnostics(
            config,
            new("SB010", "route.rules[0].rules[0].outbound", "引用了不存在的 outbound 或 endpoint。"),
            new("SB011", "route.rules[0].rules[0].rule_set[0]", "引用了不存在的 rule-set。"),
            new("SB034", "route.rules[0].rules[0].inbound[0]", "引用了不存在的 inbound。"));
    }

    private void AssertDiagnostics(
        SingboxConfig config,
        params ConfigDiagnostic[] expected)
    {
        var exception = Assert.Throws<ConfigValidationException>(
            () => validator.Validate(config));
        Assert.That(exception!.Diagnostics, Is.EqualTo(expected));
    }

    private static SingboxConfig CreateValidConfig() =>
        new()
        {
            Dns = new DnsConfig
            {
                Servers =
                [
                    new HttpsDnsServer
                    {
                        Tag = "dns",
                        ServerAddress = "1.1.1.1",
                        TlsConfig = new DnsTlsConfig
                        {
                            ServerName = "cloudflare-dns.com"
                        }
                    }
                ],
                Rules =
                [
                    new DnsRule
                    {
                        Action = DnsRuleAction.Route,
                        Server = "dns"
                    }
                ]
            },
            HttpClients =
            [
                new HttpClientConfig
                {
                    Tag = "http",
                    Detour = "direct"
                }
            ],
            Inbounds =
            [
                new Inbound
                {
                    Type = "mixed",
                    Tag = "tun",
                    ListenPort = 1080
                }
            ],
            Outbounds =
            [
                CreateDirectOutbound(),
                new SelectorOutbound
                {
                    Tag = "selector",
                    Outbounds = ["direct"],
                    Default = "direct"
                }
            ],
            Route = new RouteConfig
            {
                Final = "selector",
                DefaultHttpClient = "http",
                RuleSet =
                [
                    new SingboxRuleSet
                    {
                        Type = RuleSetType.Remote,
                        Tag = "rules",
                        Format = RuleSetFormat.Binary,
                        Url = "https://example.test/rules.srs",
                        HttpClient = "http"
                    }
                ],
                Rules =
                [
                    new RouteRule
                    {
                        Inbound = ["tun"],
                        RuleSet = ["rules"],
                        Action = RouteRuleAction.Route,
                        Outbound = "direct"
                    }
                ]
            },
            Experimental = new ExperimentalConfig
            {
                CacheFile = new CacheFileConfig
                {
                    Enabled = true,
                    Path = "cache.db",
                    CacheId = new string('a', 64)
                }
            }
        };

    private static DirectOutbound CreateDirectOutbound() =>
        new()
        {
            Tag = "direct",
            DomainResolver = "dns"
        };
}
