using BoxForge.Configuration;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

public sealed class StrictParsingTests
{
    [Fact]
    public void Prepare_AcceptsProtocolTypeCaseInsensitively()
    {
        string yaml = TestInfrastructure.ValidShadowsocksYaml.Replace(
            "type: ss",
            "type: SS",
            StringComparison.Ordinal);
        using var services = TestInfrastructure.CreateServices();
        var conversion = services.GetRequiredService<ConversionService>();

        PreparedConversion prepared = conversion.Prepare(yaml, strictNodeValidation: true);

        Assert.Single(prepared.Nodes.Outbounds);
    }

    [Theory]
    [InlineData("skip-cert-verify: perhaps", "true 或 false")]
    [InlineData("obfs: salamander", "obfs-password")]
    public void Prepare_RejectsMalformedPresentFields(string field, string expected)
    {
        string yaml = TestInfrastructure.ValidHysteria2Yaml.Replace(
            "    sni: example.com",
            $"    sni: example.com\n    {field}",
            StringComparison.Ordinal);
        using var services = TestInfrastructure.CreateServices();
        var conversion = services.GetRequiredService<ConversionService>();

        NodeParseException exception = Assert.Throws<NodeParseException>(
            () => conversion.Prepare(yaml, strictNodeValidation: true));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_RejectsMissingNodeName()
    {
        string yaml = TestInfrastructure.ValidShadowsocksYaml.Replace(
            "  - name: test-node\n    type: ss",
            "  - type: ss",
            StringComparison.Ordinal);
        using var services = TestInfrastructure.CreateServices();
        var conversion = services.GetRequiredService<ConversionService>();

        NodeParseException exception = Assert.Throws<NodeParseException>(
            () => conversion.Prepare(yaml, strictNodeValidation: true));

        Assert.Contains("name", exception.Message, StringComparison.Ordinal);
        Assert.Contains("<未命名>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RejectInvalidBooleanInsteadOfUsingDefault()
    {
        using var services = TestInfrastructure.CreateServices(
            new Dictionary<string, string?>
            {
                ["Tailscale:Enabled"] = "maybe"
            });
        var options = services.GetRequiredService<IOptions<TailscaleOptions>>();

        Assert.Throws<FormatException>(() => _ = options.Value);
    }
}
