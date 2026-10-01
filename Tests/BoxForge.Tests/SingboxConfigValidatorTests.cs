using BoxForge.Exceptions;
using BoxForge.Configuration;
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
        Assert.DoesNotThrow(() => validator.Validate(CreateValidConfig(), AddressFamilyPolicy.DualStack));
    }

    [TestCase("grpc", "gRPC transport 必须保留已映射的非空服务名。")]
    [TestCase("httpupgrade", "HTTPUpgrade transport 必须保留源请求 Host。")]
    public void TransportMustRetainRequiredMappedValues(string kind, string message)
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Outbounds = [.. valid.Outbounds, new VlessOutbound
            {
                Tag = "transport", Server = "node.example.com", ServerPort = 443,
                Uuid = "00000000-0000-4000-8000-000000000001",
                Transport = kind == "grpc" ? new GrpcTransport { ServiceName = "" } : new HttpUpgradeTransport { Host = "" }
            }]
        };
        AssertDiagnostics(config, new ConfigDiagnostic("SB079", $"outbounds[{valid.Outbounds.Count}].transport", message));
    }

    [Test]
    public void UnregisteredTransportModelCannotReachSerialization()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Outbounds = [.. valid.Outbounds, new VlessOutbound
            {
                Tag = "transport", Server = "node.example.com", ServerPort = 443,
                Uuid = "00000000-0000-4000-8000-000000000001", Transport = new UnregisteredTransport()
            }]
        };
        AssertDiagnostics(config, new ConfigDiagnostic("SB079", $"outbounds[{valid.Outbounds.Count}].transport",
            "transport model 未注册对应的 sing-box type。"));
    }

    private sealed record UnregisteredTransport : V2RayTransport;

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
    public void GroupedRuleSetTagsAreAvailableToDnsAndRouteRules()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxRuleSet grouped = valid.Route.RuleSet[0] with
        {
            Tag = ["rules", "secondary-rules"],
            Url = "https://example.test/{tag}.srs"
        };
        SingboxConfig config = valid with
        {
            Dns = valid.Dns with
            {
                Rules =
                [
                    .. valid.Dns.Rules,
                    new DnsRule
                    {
                        RuleSet = ["secondary-rules"],
                        Action = DnsRuleAction.Route,
                        Server = "dns"
                    }
                ]
            },
            Route = valid.Route with
            {
                RuleSet = [grouped],
                Rules =
                [
                    valid.Route.Rules[0] with
                    {
                        RuleSet = ["secondary-rules"]
                    }
                ]
            }
        };

        Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.DualStack));
    }

    [Test]
    public void RuleSetTagsMustBePresentAndUnique()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Route = valid.Route with
            {
                RuleSet =
                [
                    valid.Route.RuleSet[0] with { Tag = [] },
                    valid.Route.RuleSet[0] with
                    {
                        Tag = ["duplicate", "", "duplicate"],
                        Url = "https://example.test/{tag}.srs"
                    },
                    valid.Route.RuleSet[0] with { Tag = ["duplicate"] }
                ],
                Rules = []
            }
        };

        AssertDiagnostics(
            config,
            new("SB008", "route.rule_set[0].tag", "标签不能为空。"),
            new("SB008", "route.rule_set[1].tag[1]", "标签不能为空。"),
            new("SB009", "route.rule_set[1].tag[2]", "标签 'duplicate' 重复。"),
            new("SB009", "route.rule_set[2].tag", "标签 'duplicate' 重复。"));
    }

    [Test]
    public void GroupedRemoteRuleSetRequiresTagPlaceholder()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Route = valid.Route with
            {
                RuleSet =
                [
                    valid.Route.RuleSet[0] with
                    {
                        Tag = ["rules", "secondary-rules"]
                    }
                ],
                Rules = []
            }
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB072",
                "route.rule_set[0].url",
                "包含多个 tag 的远程 rule-set URL 必须使用 {tag} 占位符。"));
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
    public void OutboundTagsMustBeUnique()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Outbounds = [.. valid.Outbounds, CreateDirectOutbound()]
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB009",
                "outbounds[2].tag",
                "标签 'direct' 重复。"));
    }

    [Test]
    public void EndpointTagsMustBeUnique()
    {
        SingboxConfig config = CreateValidConfig() with
        {
            Endpoints = [CreateEndpoint("endpoint"), CreateEndpoint("endpoint")]
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB009",
                "endpoints[1].tag",
                "标签 'endpoint' 重复。"));
    }

    [Test]
    public void InboundTagsMustBeUnique()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Inbounds = [.. valid.Inbounds, valid.Inbounds[0]]
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB009",
                "inbounds[1].tag",
                "标签 'tun' 重复。"));
    }

    [Test]
    public void OutboundAndEndpointRouteTargetTagsMustNotCollide()
    {
        SingboxConfig config = CreateValidConfig() with
        {
            Endpoints = [CreateEndpoint("direct")]
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB009",
                "endpoints[0].tag",
                "标签 'direct' 与 outbound tag 重复。"));
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
    public void ApiServiceMustKeepTheLocalControlPlaneContract()
    {
        SingboxConfig config = CreateValidConfig() with
        {
            Services =
            [
                new ApiService
                {
                    Tag = "api",
                    Listen = "0.0.0.0",
                    ListenPort = 8080,
                    AccessControlAllowOrigin = ["*"],
                    AccessControlAllowPrivateNetwork = true,
                    Dashboard = new ApiDashboardConfig
                    {
                        Enabled = true,
                        Path = "dashboard",
                        HttpClient = "missing-http"
                    }
                }
            ]
        };

        AssertDiagnostics(
            config,
            new("SB066", "services[0].listen", "sing-box API 必须仅监听 127.0.0.1。"),
            new("SB067", "services[0].listen_port", "sing-box API 监听端口必须是 9090。"),
            new("SB068", "services[0].access_control_allow_origin", "sing-box API 只允许固定的本机 Dashboard origin。"),
            new("SB069", "services[0].access_control_allow_private_network", "sing-box API 不得允许浏览器私网跨域访问。"),
            new("SB071", "services[0].dashboard.http_client", "引用了不存在的 HTTP client。"));
    }

    [Test]
    public void RoutePreferredByTargetsMustExist()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Route = valid.Route with
            {
                Rules =
                [
                    .. valid.Route.Rules,
                    new RouteRule
                    {
                        PreferredBy = ["missing-target"],
                        Action = RouteRuleAction.Route,
                        Outbound = "direct"
                    }
                ]
            }
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB065",
                "route.rules[1].preferred_by[0]",
                "引用了不存在的 outbound 或 endpoint。"));
    }

    [Test]
    public void RouteIpVersionAndNoDropMustUseSupportedSemantics()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Route = valid.Route with
            {
                Rules =
                [
                    .. valid.Route.Rules,
                    new RouteRule
                    {
                        IpVersion = 5,
                        NoDrop = true,
                        Action = RouteRuleAction.Route,
                        Outbound = "direct"
                    }
                ]
            }
        };

        AssertDiagnostics(
            config,
            new("SB073", "route.rules[1].ip_version", "ip_version 只能是 4 或 6。"),
            new("SB074", "route.rules[1].no_drop", "只有 reject 动作可以指定 no_drop。"));
    }

    [Test]
    public void ControlPlaneMustUseFreshIpv4OnlyResolutionAndRejectIpv6Literals()
    {
        SingboxConfig valid = CreateValidIpv4Config();
        SingboxConfig config = valid with
        {
            Outbounds =
            [
                .. valid.Outbounds,
                new VlessOutbound
                {
                    Tag = "ipv6-proxy",
                    Server = "2001:db8::1",
                    ServerPort = 443,
                    Uuid = "00000000-0000-4000-8000-000000000001"
                }
            ],
            Route = valid.Route with
            {
                DefaultDomainResolver = new DnsResolverOptions
                {
                    Server = "dns",
                    Strategy = DnsStrategy.PreferIpv4
                }
            }
        };

        AssertPolicyDiagnostics(
            config,
            AddressFamilyPolicy.Ipv4Only,
            new ConfigDiagnostic(
                "SB061",
                "route.default_domain_resolver.strategy",
                "代理节点域名必须使用 ipv4_only 解析策略。"),
            new ConfigDiagnostic(
                "SB064",
                "route.default_domain_resolver.disable_optimistic_cache",
                "代理节点域名解析必须禁用 optimistic 过期缓存。"),
            new ConfigDiagnostic(
                "SB062",
                "outbounds[2].server",
                "代理节点不能使用 IPv6 字面量地址。"));
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
                DefaultDomainResolver = new DnsResolverOptions
                {
                    Server = "dns",
                    Strategy = DnsStrategy.Ipv4Only,
                    DisableOptimisticCache = true
                },
                RuleSet =
                [
                    new SingboxRuleSet
                    {
                        Tag = ["broken-rule-set"],
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
            () => validator.Validate(config, AddressFamilyPolicy.DualStack));

        Assert.That(exception!.Diagnostics, Is.EqualTo(new ConfigDiagnostic[]
        {
            new("SB001", "route.final", "不存在对应的 outbound 或 endpoint。"),
            new("SB014", "route.default_http_client", "引用了不存在的 HTTP client。"),
            new("SB015", "http_clients[0].detour", "引用了不存在的 outbound 或 endpoint。"),
            new("SB019", "outbounds[0].server", "代理服务器地址不能为空。"),
            new("SB020", "outbounds[0].server_port", "代理节点必须配置有效端口。"),
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
    public void UrlTestRequiresExistingUniqueLeafProxyCandidates()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Outbounds =
            [
                .. valid.Outbounds,
                CreateProxyOutbound("leaf"),
                new UrlTestOutbound
                {
                    Tag = "auto",
                    Outbounds = ["missing", "selector", "leaf", "leaf"]
                }
            ]
        };

        AssertDiagnostics(
            config,
            new("SB075", "outbounds[3].outbounds[0]", "urltest 引用了不存在的目标。"),
            new("SB076", "outbounds[3].outbounds[1]", "urltest 只能引用真实代理节点。"),
            new("SB077", "outbounds[3].outbounds", "urltest 不能包含重复目标。"));
    }

    [Test]
    public void UrlTestRequiresAtLeastTwoCandidates()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            Outbounds =
            [
                .. valid.Outbounds,
                CreateProxyOutbound("leaf"),
                new UrlTestOutbound
                {
                    Tag = "auto",
                    Outbounds = ["leaf"]
                }
            ]
        };

        AssertDiagnostics(
            config,
            new ConfigDiagnostic(
                "SB078",
                "outbounds[3].outbounds",
                "urltest 至少需要两个真实代理节点。"));
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
                        Tag = SingboxTags.TailscaleDns,
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

    [Test]
    public void Ipv4OnlyCompleteGuardsPass()
    {
        Assert.DoesNotThrow(() => validator.Validate(CreateValidIpv4Config(), AddressFamilyPolicy.Ipv4Only));
    }

    [Test]
    public void ExplicitFalsePrivateFlagDoesNotNarrowIpv6GuardInOfficialCore()
    {
        SingboxConfig valid = CreateValidIpv4Config();
        var config = valid with
        {
            Route = valid.Route with
            {
                Rules = [valid.Route.Rules[0] with { IpIsPrivate = false }, .. valid.Route.Rules.Skip(1)]
            }
        };
        Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.Ipv4Only));
    }

    [TestCase(0, "SB080")]
    [TestCase(2, "SB081")]
    public void Ipv4OnlyRequiresEarlyAndPostResolveGuards(int removeIndex, string code)
    {
        SingboxConfig valid = CreateValidIpv4Config();
        var config = valid with
        {
            Route = valid.Route with
            {
                Rules = valid.Route.Rules.Where((_, index) => index != removeIndex).ToList()
            }
        };
        var error = Assert.Throws<ConfigValidationException>(() => validator.Validate(config, AddressFamilyPolicy.Ipv4Only));
        Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { code }));
    }

    [Test]
    public void Ipv4OnlyRejectGuardCannotBeNarrowedToPrivateOrSingleNetwork()
    {
        SingboxConfig valid = CreateValidIpv4Config();
        foreach (RouteRule restricted in new[]
        {
            valid.Route.Rules[0] with { IpIsPrivate = true },
            valid.Route.Rules[0] with { Network = ["tcp"] },
            valid.Route.Rules[0] with { Inbound = ["other"] },
            valid.Route.Rules[0] with { Invert = true }
        })
        {
            var config = valid with { Route = valid.Route with { Rules = [restricted, .. valid.Route.Rules.Skip(1)] } };
            var error = Assert.Throws<ConfigValidationException>(() => validator.Validate(config, AddressFamilyPolicy.Ipv4Only));
            Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("SB080"));
        }
    }

    [Test]
    public void Ipv4OnlyRequiresUnconditionalAaaaBlockBeforeOtherDnsRules()
    {
        SingboxConfig valid = CreateValidIpv4Config();
        foreach (List<DnsRule> rules in new[]
        {
            valid.Dns.Rules.Skip(1).ToList(),
            valid.Dns.Rules.AsEnumerable().Reverse().ToList(),
            new List<DnsRule> { valid.Dns.Rules[0] with { Domain = ["example.com"] }, valid.Dns.Rules[1] }
        })
        {
            var config = valid with { Dns = valid.Dns with { Rules = rules } };
            var error = Assert.Throws<ConfigValidationException>(() => validator.Validate(config, AddressFamilyPolicy.Ipv4Only));
            Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "SB082" }));
        }
    }

    [Test]
    public void DualStackDoesNotRequireIpv4ResolutionOrLeakGuards()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Route = valid.Route with { DefaultDomainResolver = valid.Route.DefaultDomainResolver! with { Strategy = null } },
            Outbounds = [.. valid.Outbounds, CreateProxyOutbound("ipv6") with { Server = "2001:db8::1" }]
        };
        Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.DualStack));
    }

    private void AssertDiagnostics(
        SingboxConfig config,
        params ConfigDiagnostic[] expected)
        => AssertPolicyDiagnostics(config, AddressFamilyPolicy.DualStack, expected);

    private void AssertPolicyDiagnostics(
        SingboxConfig config,
        AddressFamilyPolicy addressFamily,
        params ConfigDiagnostic[] expected)
    {
        var exception = Assert.Throws<ConfigValidationException>(
            () => validator.Validate(config, addressFamily));
        Assert.That(exception!.Diagnostics, Is.EqualTo(expected));
    }

    private static SingboxConfig CreateValidIpv4Config()
    {
        SingboxConfig valid = CreateValidConfig();
        return valid with
        {
            Dns = valid.Dns with
            {
                Rules = [new DnsRule
                {
                    QueryType = ["AAAA"], Action = DnsRuleAction.Predefined, Rcode = DnsResponseCode.NoError
                }, .. valid.Dns.Rules]
            },
            Route = valid.Route with
            {
                Rules = [new RouteRule { IpVersion = 6, Action = RouteRuleAction.Reject },
                    new RouteRule { RuleSet = ["rules"], Action = RouteRuleAction.Resolve, Strategy = DnsStrategy.Ipv4Only },
                    new RouteRule { IpVersion = 6, Action = RouteRuleAction.Reject }, .. valid.Route.Rules]
            }
        };
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
                DefaultDomainResolver = new DnsResolverOptions
                {
                    Server = "dns",
                    Strategy = DnsStrategy.Ipv4Only,
                    DisableOptimisticCache = true
                },
                RuleSet =
                [
                    new SingboxRuleSet
                    {
                        Type = RuleSetType.Remote,
                        Tag = ["rules"],
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
        new() { Tag = "direct" };

    private static TailscaleEndpoint CreateEndpoint(string tag) =>
        new()
        {
            Tag = tag,
            StateDirectory = "tailscale",
            AcceptRoutes = true,
            TaildropDirectory = "Taildrop"
        };

    private static ShadowsocksOutbound CreateProxyOutbound(string tag) =>
        new()
        {
            Tag = tag,
            Server = "node.example.com",
            ServerPort = 443,
            Method = "aes-128-gcm",
            Password = "test-only"
        };
}
