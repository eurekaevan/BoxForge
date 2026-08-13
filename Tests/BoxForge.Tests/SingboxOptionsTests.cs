using BoxForge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoxForge.Tests;

[TestFixture]
public sealed class SingboxOptionsTests
{
    private const string AdGuardDnsRuleSetUrl =
        "https://sublinks.skuld.workers.dev/rules/adguard-dns.srs";

    [Test]
    public void NestedConfigurationProvidesAdGuardRuleSetUrl()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Singbox:AdGuardDnsRuleSetUrl"] = AdGuardDnsRuleSetUrl
            })
            .Build();
        var services = new ServiceCollection();

        services.AddBoxForgeOptions(configuration);

        SingboxOptions options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<SingboxOptions>>()
            .Value;
        Assert.That(options.AdGuardDnsRuleSetUrl, Is.EqualTo(AdGuardDnsRuleSetUrl));
    }

    [Test]
    public void HttpsAdGuardRuleSetUrlIsAccepted()
    {
        ValidateOptionsResult result = Validate(AdGuardDnsRuleSetUrl);

        Assert.That(result.Succeeded, Is.True);
    }

    [TestCase("", "AdGuard DNS 规则集地址不能为空。")]
    [TestCase("rules/adguard-dns.srs", "AdGuard DNS 规则集地址必须是绝对 URL。")]
    [TestCase("http://example.com/adguard-dns.srs", "AdGuard DNS 规则集地址必须使用 HTTPS。")]
    public void NonHttpsAdGuardRuleSetUrlIsRejected(
        string url,
        string expectedError)
    {
        ValidateOptionsResult result = Validate(url);

        Assert.Multiple(() =>
        {
            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Is.EqualTo(expectedError));
        });
    }

    private static ValidateOptionsResult Validate(string url) =>
        new SingboxOptionsValidator().Validate(
            null,
            new SingboxOptions
            {
                AdGuardDnsRuleSetUrl = url
            });
}
