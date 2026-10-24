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

    [TestCase("none")]
    [TestCase("chrome120")]
    [TestCase("chrome_psk")]
    public void InvalidUtlsModelCannotBypassSourceValidation(string fingerprint)
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Outbounds = [.. valid.Outbounds, new TrojanOutbound
            {
                Tag = "invalid-utls", Server = "node.example.com", ServerPort = 443, Password = "test-only",
                Tls = new OutboundTls { Enabled = true, ServerName = "node.example.com", Utls = new Utls { Enabled = true, Fingerprint = fingerprint } }
            }]
        };
        AssertDiagnostics(config, new ConfigDiagnostic("SB089", $"outbounds[{valid.Outbounds.Count}].tls.utls.fingerprint",
            "uTLS 指纹不在 BoxForge 的等价映射支持范围内。"));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(null, true)]
    public void RealityRequiresTlsAndUtlsInTheTargetModel(bool? tlsEnabled, bool utlsEnabled)
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Outbounds = [.. valid.Outbounds, new VlessOutbound
            {
                Tag = "invalid-reality", Server = "node.example.com", ServerPort = 443,
                Uuid = "00000000-0000-4000-8000-000000000001",
                Tls = new OutboundTls
                {
                    Enabled = tlsEnabled, ServerName = "node.example.com",
                    Utls = new Utls { Enabled = utlsEnabled, Fingerprint = "chrome" },
                    Reality = new OutboundReality { Enabled = true, PublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", ShortId = "00" }
                }
            }]
        };
        AssertDiagnostics(config, new ConfigDiagnostic("SB090", $"outbounds[{valid.Outbounds.Count}].tls.reality",
            "Reality 必须启用 TLS 和有效的 uTLS。"));
    }

    [Test]
    public void WhitespacePasswordsAreValidAuthenticationMaterial()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Outbounds = [.. valid.Outbounds,
                new TrojanOutbound { Tag = "trojan-spaces", Server = "node.example.com", ServerPort = 443, Password = "  " },
                new Hysteria2Outbound { Tag = "hy2-spaces", Server = "node.example.com", ServerPort = 443, Password = "  " },
                new AnyTlsOutbound { Tag = "anytls-spaces", Server = "node.example.com", ServerPort = 443, Password = "  " }]
        };
        Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.DualStack));
    }

    [Test]
    public void ValidConfigurationPasses()
    {
        Assert.DoesNotThrow(() => validator.Validate(CreateValidConfig(), AddressFamilyPolicy.DualStack));
    }

    [Test]
    public void DnsReferencesReportExactPathsAndUseSeparateNamespaces()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Dns = valid.Dns with
            {
                Final = "missing-dns",
                Rules = [new DnsRule
                {
                    Action = DnsRuleAction.Route,
                    Server = "dns",
                    RuleSet = ["missing-rules"],
                    PreferredBy = ["direct", "dns"],
                    MatchResponse = "dns"
                }]
            }
        };

        AssertDiagnostics(config,
            new("SB084", "dns.final", "引用了不存在的 DNS server。"),
            new("SB011", "dns.rules[0].rule_set[0]", "引用了不存在的 rule-set。"),
            new("SB087", "dns.rules[0].preferred_by[0]", "引用了不存在的 DNS server。"),
            new("SB088", "dns.rules[0].preferred_by[1]", "DNS server 不支持 preferred_by 域名匹配。"),
            new("SB086", "dns.rules[0].match_response", "必须引用前置 evaluate 规则定义的响应标签。"));
    }

    [TestCase("")]
    [TestCase("dns")]
    public void DnsFinalMaySelectAnExistingServerOrUseTheCoreDefault(string final)
    {
        SingboxConfig valid = CreateValidConfig();
        Assert.DoesNotThrow(() => validator.Validate(valid with
        {
            Dns = valid.Dns with { Final = final }
        }, AddressFamilyPolicy.DualStack));
    }

    [Test]
    public void DnsPreferredBySupportsTheTailscaleDnsTransport()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Endpoints = [CreateEndpoint("tailscale")],
            Dns = valid.Dns with
            {
                Servers = [.. valid.Dns.Servers,
                    new TailscaleDnsServer { Tag = "tail-dns", EndpointTag = "tailscale" }],
                Rules = [new DnsRule
                {
                    PreferredBy = ["tail-dns"], Action = DnsRuleAction.Route, Server = "tail-dns"
                }]
            }
        };
        Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.DualStack));
    }

    [Test]
    public void DnsResponsesCanOnlyReferencePrecedingNamedEvaluateRules()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Dns = valid.Dns with
            {
                Rules =
                [
                    new DnsRule { Action = DnsRuleAction.Evaluate, Server = "dns", Tag = "primary" },
                    new DnsRule
                    {
                        Action = DnsRuleAction.Evaluate, Server = "dns", Tag = "fallback", MatchResponse = "primary"
                    },
                    new DnsRule { Action = DnsRuleAction.Respond, MatchResponse = "fallback" }
                ]
            }
        };
        Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.DualStack));
    }

    [TestCase("missing")]
    [TestCase("later")]
    [TestCase("")]
    public void MissingOrForwardDnsResponseTagsAreRejected(string reference)
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Dns = valid.Dns with
            {
                Rules =
                [
                    new DnsRule { Action = DnsRuleAction.Respond, MatchResponse = reference },
                    new DnsRule { Action = DnsRuleAction.Evaluate, Server = "dns", Tag = "later" }
                ]
            }
        };
        AssertDiagnostics(config,
            new ConfigDiagnostic("SB086", "dns.rules[0].match_response", "必须引用前置 evaluate 规则定义的响应标签。"));
    }

    [Test]
    public void DnsResponseTagsAreCaseSensitive()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Dns = valid.Dns with
            {
                Rules =
                [
                    new DnsRule { Action = DnsRuleAction.Evaluate, Server = "dns", Tag = "primary" },
                    new DnsRule { Action = DnsRuleAction.Respond, MatchResponse = "PRIMARY" }
                ]
            }
        };
        AssertDiagnostics(config,
            new ConfigDiagnostic("SB086", "dns.rules[1].match_response", "必须引用前置 evaluate 规则定义的响应标签。"));
    }

    [Test]
    public void DnsEvaluateCannotReferenceItselfOrRedefineAResponseTag()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Dns = valid.Dns with
            {
                Rules =
                [
                    new DnsRule { Action = DnsRuleAction.Evaluate, Server = "dns", Tag = "same", MatchResponse = "same" },
                    new DnsRule { Action = DnsRuleAction.Evaluate, Server = "dns", Tag = "same" }
                ]
            }
        };
        AssertDiagnostics(config,
            new("SB086", "dns.rules[0].match_response", "必须引用前置 evaluate 规则定义的响应标签。"),
            new("SB085", "dns.rules[1].tag", "evaluate 响应标签重复。"));
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
            new("SB009", "route.rule_set[1].tag[2]", "标签重复。"),
            new("SB009", "route.rule_set[2].tag", "标签重复。"));
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
            new("SB009", "http_clients[2].tag", "标签重复。"));
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
                "标签重复。"));
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
                "标签重复。"));
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
                "标签重复。"));
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
                "标签与 outbound tag 重复。"));
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

    [TestCase(AddressFamilyPolicy.Ipv4Only)]
    [TestCase(AddressFamilyPolicy.DirectDualStack)]
    public void ControlPlaneMustUseFreshIpv4OnlyResolutionAndRejectIpv6Literals(AddressFamilyPolicy addressFamily)
    {
        SingboxConfig valid = addressFamily == AddressFamilyPolicy.Ipv4Only ? CreateValidIpv4Config() : CreateValidDirectDualStackConfig();
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
            addressFamily,
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
    public void CoreDefaultCachePathIsOutsideBoxForgeValidation()
    {
        SingboxConfig valid = CreateValidConfig();
        var config = valid with
        {
            Experimental = valid.Experimental! with
            {
                CacheFile = valid.Experimental!.CacheFile! with { Path = "" }
            }
        };

        Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.DualStack));
    }

    [Test]
    public void ProjectDiagnosticsPreserveCodesPathsMessagesAndOrder()
    {
        SingboxConfig valid = CreateValidConfig();
        SingboxConfig config = valid with
        {
            HttpClients = [valid.HttpClients[0] with { Detour = "missing-target" }],
            Outbounds =
            [
                new VlessOutbound
                {
                    Tag = "proxy", Server = "node.example.com", ServerPort = 443,
                    Uuid = "00000000-0000-4000-8000-000000000001",
                    Tls = new OutboundTls { Enabled = true, ServerName = "" }
                }
            ],
            Dns = valid.Dns with
            {
                Rules =
                [
                    new DnsRule { Action = DnsRuleAction.Route, Server = "missing-dns" },
                    new DnsRule { Action = DnsRuleAction.Evaluate, Server = "dns" }
                ]
            },
            Route = valid.Route with
            {
                Final = "missing-target",
                DefaultHttpClient = "missing-http",
                Rules =
                [
                    new RouteRule
                    {
                        Action = RouteRuleAction.Route,
                        Outbound = "missing-route-target",
                        RuleSet = ["missing-rule-set"],
                        Inbound = ["missing-inbound"]
                    }
                ]
            },
            Experimental = valid.Experimental! with
            {
                CacheFile = valid.Experimental!.CacheFile! with { CacheId = "short" }
            }
        };

        AssertDiagnostics(config,
            new("SB001", "route.final", "不存在对应的 outbound 或 endpoint。"),
            new("SB014", "route.default_http_client", "引用了不存在的 HTTP client。"),
            new("SB015", "http_clients[0].detour", "引用了不存在的 outbound 或 endpoint。"),
            new("SB044", "outbounds[0].tls.server_name", "TLS server_name 不能为空。"),
            new("SB007", "dns.rules[0].server", "引用了不存在的 DNS server。"),
            new("SB027", "dns.rules[1].tag", "evaluate 响应标签不能为空。"),
            new("SB059", "experimental.cache_file.cache_id", "cache_id 必须是完整的 64 位 SHA-256 十六进制字符串。"),
            new("SB010", "route.rules[0].outbound", "引用了不存在的 outbound 或 endpoint。"),
            new("SB011", "route.rules[0].rule_set[0]", "引用了不存在的 rule-set。"),
            new("SB034", "route.rules[0].inbound[0]", "引用了不存在的 inbound。"));
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
                        TlsConfig = new DnsTlsConfig { Enabled = true, ServerName = "" }
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

    [TestCase("GSG", "")]
    [TestCase("GSSG", "")]
    [TestCase("GG", "")]
    [TestCase("GGU", "")]
    [TestCase("G", "SB081")]
    [TestCase("GGS", "SB081")]
    [TestCase("GSGS", "SB081")]
    [TestCase("SGG", "SB080")]
    [TestCase("SG", "SB080")]
    [TestCase("S", "SB080,SB081")]
    [TestCase("", "SB080,SB081")]
    public void Ipv4OnlyGuardsRespectActualOrderAndLastScopedResolve(string order, string expectedCodes)
    {
        SingboxConfig valid = CreateValidIpv4Config();
        List<RouteRule> rules = order.Select(marker => marker switch
        {
            'G' => valid.Route.Rules[0],
            'S' => valid.Route.Rules[1],
            'U' => valid.Route.Rules[1] with { RuleSet = null },
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        }).ToList();
        var config = valid with { Route = valid.Route with { Rules = rules } };

        if (expectedCodes.Length == 0)
        {
            Assert.DoesNotThrow(() => validator.Validate(config, AddressFamilyPolicy.Ipv4Only));
        }
        else
        {
            var error = Assert.Throws<ConfigValidationException>(
                () => validator.Validate(config, AddressFamilyPolicy.Ipv4Only));
            Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(expectedCodes.Split(',')));
        }
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

    [TestCase("missing", DnsStrategy.Ipv4Only, "SB004")]
    [TestCase(SingboxTags.DirectAliDns, DnsStrategy.PreferIpv4, "SB061")]
    public void DirectResolverMustExistAndRespectIpv4Only(string server, DnsStrategy strategy, string code)
    {
        SingboxConfig valid = CreateValidIpv4Config();
        var config = valid with
        {
            Outbounds = valid.Outbounds.Select(outbound => outbound is DirectOutbound direct
                ? direct with { DomainResolver = new DnsResolverOptions { Server = server, Strategy = strategy } }
                : outbound).ToList()
        };
        var error = Assert.Throws<ConfigValidationException>(() => validator.Validate(config, AddressFamilyPolicy.Ipv4Only));
        Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code));
    }

    [Test]
    public void AutoRequiresConditionalGuardsWithoutAnUnconditionalDnsFamilyRestriction()
    {
        SingboxConfig valid = CreateValidAutoConfig();
        Assert.DoesNotThrow(() => validator.Validate(valid, AddressFamilyPolicy.Auto));
        foreach (RouteRule restricted in new[]
        {
            new RouteRule { IpVersion = 6, Action = RouteRuleAction.Reject },
            valid.Route.Rules[0] with { Mode = RouteLogicalMode.Or },
            valid.Route.Rules[0] with { Network = ["tcp"] },
            valid.Route.Rules[0] with { Rules = [valid.Route.Rules[0].Rules![0]] },
            valid.Route.Rules[0] with
            {
                Rules = [valid.Route.Rules[0].Rules![0], valid.Route.Rules[0].Rules![1] with { Invert = false }]
            }
        })
        {
            var config = valid with { Route = valid.Route with { Rules = [restricted, .. valid.Route.Rules.Skip(1)] } };
            var error = Assert.Throws<ConfigValidationException>(() => validator.Validate(config, AddressFamilyPolicy.Auto));
            Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("SB080"));
        }
        foreach (DnsRule restricted in new[]
        {
            new DnsRule { QueryType = ["AAAA"], Action = DnsRuleAction.Predefined, Rcode = DnsResponseCode.NoError },
            valid.Dns.Rules[0] with { Mode = RouteLogicalMode.Or },
            valid.Dns.Rules[0] with { Domain = ["example.com"] },
            valid.Dns.Rules[0] with
            {
                Rules = [valid.Dns.Rules[0].Rules![0], valid.Dns.Rules[0].Rules![1] with { DefaultInterfaceAddress = ["::/0"] }]
            }
        })
        {
            var config = valid with { Dns = valid.Dns with { Rules = [restricted, .. valid.Dns.Rules.Skip(1)] } };
            var error = Assert.Throws<ConfigValidationException>(() => validator.Validate(config, AddressFamilyPolicy.Auto));
            Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("SB092"));
        }
        var forcedIpv4 = valid with { Dns = valid.Dns with { Strategy = DnsStrategy.Ipv4Only } };
        var forcedError = Assert.Throws<ConfigValidationException>(() => validator.Validate(forcedIpv4, AddressFamilyPolicy.Auto));
        Assert.That(forcedError!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("SB092"));
    }

    [TestCase("server", "SB007")]
    [TestCase("rule_set", "SB011")]
    [TestCase("match_response", "SB086")]
    [TestCase("action", "SB091")]
    public void DnsLogicalChildrenCannotHideInvalidReferencesOrDefineResponses(string fault, string expectedCode)
    {
        SingboxConfig valid = CreateValidConfig();
        DnsRule child = fault switch
        {
            "server" => new DnsRule { Server = "missing" },
            "rule_set" => new DnsRule { RuleSet = ["missing"] },
            "match_response" => new DnsRule { MatchResponse = "missing" },
            _ => new DnsRule { Action = DnsRuleAction.Evaluate, Tag = "nested" }
        };
        var config = valid with
        {
            Dns = valid.Dns with
            {
                Rules = [new DnsRule
                {
                    Type = RouteRuleType.Logical, Mode = RouteLogicalMode.And, Rules = [child],
                    Action = DnsRuleAction.Route, Server = "dns"
                }]
            }
        };
        var error = Assert.Throws<ConfigValidationException>(() => validator.Validate(config, AddressFamilyPolicy.DualStack));
        Assert.That(error!.Diagnostics.Single().Code, Is.EqualTo(expectedCode));
        Assert.That(error.Diagnostics.Single().Path, Does.StartWith("dns.rules[0].rules[0]"));
    }

    [Test]
    public void DirectDualStackRequiresInterfaceGuardsAndShieldsEveryProxyRoute()
    {
        SingboxConfig valid = CreateValidDirectDualStackConfig();
        Assert.DoesNotThrow(() => validator.Validate(valid, AddressFamilyPolicy.DirectDualStack));

        List<RouteRule> rules = valid.Route.Rules;
        foreach (var (name, mutated, code) in new (string, List<RouteRule>, string)[]
        {
            ("final guard removed", [.. rules.Take(rules.Count - 1)], "SB093"),
            ("final guard narrowed", [.. rules.Take(rules.Count - 1), rules[^1] with { Network = ["tcp"] }], "SB093"),
            ("scoped guard removed", [.. rules.Where(rule => rule.RuleSet is null || rule.IpVersion != 6)], "SB094"),
            ("scoped guard too narrow", [.. rules.Select(rule => rule.IpVersion == 6 && rule.RuleSet is not null
                ? rule with { RuleSet = ["other"] } : rule)], "SB094"),
            ("scoped guard after route", [.. rules.Take(3), rules[4], rules[3], .. rules.Skip(5)], "SB094")
        })
        {
            var config = valid with { Route = valid.Route with { Rules = mutated } };
            var error = Assert.Throws<ConfigValidationException>(
                () => validator.Validate(config, AddressFamilyPolicy.DirectDualStack), name)!;
            Assert.That(error.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code), name);
        }

        var dualStackControlPlane = valid with
        {
            Route = valid.Route with { DefaultDomainResolver = valid.Route.DefaultDomainResolver! with { Strategy = null } }
        };
        var resolverError = Assert.Throws<ConfigValidationException>(
            () => validator.Validate(dualStackControlPlane, AddressFamilyPolicy.DirectDualStack))!;
        Assert.That(resolverError.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("SB061"));

        var directPrefersIpv4 = valid with
        {
            Outbounds = valid.Outbounds.Select(outbound => outbound is DirectOutbound direct
                ? direct with { DomainResolver = new DnsResolverOptions { Server = "dns", Strategy = DnsStrategy.PreferIpv4 } }
                : outbound).ToList()
        };
        Assert.DoesNotThrow(() => validator.Validate(directPrefersIpv4, AddressFamilyPolicy.DirectDualStack));
    }

    private static SingboxConfig CreateValidDirectDualStackConfig()
    {
        SingboxConfig auto = CreateValidAutoConfig();
        return auto with
        {
            Route = auto.Route with
            {
                Rules =
                [
                    .. auto.Route.Rules.Take(3),
                    new RouteRule { IpVersion = 6, RuleSet = ["rules"], Action = RouteRuleAction.Reject },
                    auto.Route.Rules[3],
                    new RouteRule { RuleSet = ["rules"], Action = RouteRuleAction.Route, Outbound = "selector" },
                    new RouteRule { IpVersion = 6, Action = RouteRuleAction.Reject }
                ]
            }
        };
    }

    private static SingboxConfig CreateValidAutoConfig()
    {
        SingboxConfig valid = CreateValidIpv4Config();
        return valid with
        {
            Dns = valid.Dns with
            {
                Strategy = null,
                Rules = [new DnsRule
                {
                    Type = RouteRuleType.Logical, Mode = RouteLogicalMode.And,
                    Rules = [new DnsRule { QueryType = ["AAAA"] },
                        new DnsRule { DefaultInterfaceAddress = ["2000::/3"], Invert = true }],
                    Action = DnsRuleAction.Predefined, Rcode = DnsResponseCode.NoError
                }, .. valid.Dns.Rules.Skip(1)]
            },
            Route = valid.Route with
            {
                Rules = valid.Route.Rules.Select(rule => rule.IpVersion == 6 ? new RouteRule
                {
                    Type = RouteRuleType.Logical,
                    Mode = RouteLogicalMode.And,
                    Rules = [new RouteRule { IpVersion = 6 },
                        new RouteRule { DefaultInterfaceAddress = ["2000::/3"], Invert = true }],
                    Action = RouteRuleAction.Reject
                } : rule).ToList()
            }
        };
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
                Final = "dns",
                Servers =
                [
                    new HttpsDnsServer
                    {
                        Tag = "dns",
                        ServerAddress = "1.1.1.1",
                        TlsConfig = new DnsTlsConfig
                        {
                            Enabled = true,
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
