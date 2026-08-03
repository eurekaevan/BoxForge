using BoxForge.Builders;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Models.Singbox;
using BoxForge.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BoxForge.Tests;

public sealed class SingboxConfigValidatorTests
{
    [Fact]
    public void Validate_AcceptsGeneratedConfiguration()
    {
        using var services = TestInfrastructure.CreateServices();
        SingboxConfig config = BuildConfig(services);

        services.GetRequiredService<ISingboxConfigValidator>().Validate(config);
    }

    [Fact]
    public void Validate_RejectsUnknownOrForwardDnsResponseReferences()
    {
        using var services = TestInfrastructure.CreateServices();
        SingboxConfig config = BuildConfig(services);
        config = config with
        {
            Dns = config.Dns with
            {
                Rules =
                [
                    new DnsRule
                    {
                        MatchResponse = "later",
                        IpAcceptAny = true,
                        Action = DnsRuleAction.Respond
                    },
                    new DnsRule
                    {
                        Server = "local",
                        Tag = "later",
                        Action = DnsRuleAction.Evaluate
                    }
                ]
            }
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => services.GetRequiredService<ISingboxConfigValidator>().Validate(config));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "SB029");
    }

    [Fact]
    public void Validate_RejectsInvalidRouteActionAndInboundReferences()
    {
        using var services = TestInfrastructure.CreateServices();
        SingboxConfig config = BuildConfig(services);
        config = config with
        {
            Route = config.Route with
            {
                Rules =
                [
                    new RouteRule
                    {
                        Inbound = ["missing-inbound"],
                        Action = RouteRuleAction.Route
                    }
                ]
            }
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => services.GetRequiredService<ISingboxConfigValidator>().Validate(config));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "SB034");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "SB036");
    }

    [Fact]
    public void Validate_RejectsMissingProtocolFieldsAndTruncatedCacheIdentity()
    {
        using var services = TestInfrastructure.CreateServices();
        SingboxConfig config = BuildConfig(services);
        config = config with
        {
            Outbounds =
            [
                .. config.Outbounds.Select(outbound =>
                    outbound is ShadowsocksOutbound shadowsocks
                        ? shadowsocks with { Password = "" }
                        : outbound)
            ],
            Experimental = config.Experimental! with
            {
                CacheFile = config.Experimental.CacheFile! with { CacheId = "deadbeef" }
            }
        };

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => services.GetRequiredService<ISingboxConfigValidator>().Validate(config));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "SB049");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "SB059");
    }

    private static SingboxConfig BuildConfig(ServiceProvider services)
    {
        var conversion = services.GetRequiredService<ConversionService>();
        PreparedConversion prepared = conversion.Prepare(
            TestInfrastructure.ValidShadowsocksYaml,
            strictNodeValidation: true);
        return services.GetRequiredService<ISingboxConfigBuilder>().Build(
            new SingboxBuildRequest(
                prepared.Nodes,
                TargetPlatform.Linux,
                new string('a', 64),
                "test-secret"));
    }
}
