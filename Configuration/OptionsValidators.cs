using Microsoft.Extensions.Options;

namespace BoxForge.Configuration;

public sealed class SingboxOptionsValidator : IValidateOptions<SingboxOptions>
{
    public ValidateOptionsResult Validate(string? name, SingboxOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MainProxyGroup)
            || string.IsNullOrWhiteSpace(options.Direct))
        {
            return ValidateOptionsResult.Fail("sing-box 主代理组和直连标签不能为空。");
        }

        if (string.Equals(
            options.MainProxyGroup,
            options.Direct,
            StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail("sing-box 主代理组和直连标签不能相同。");
        }

        if (string.IsNullOrWhiteSpace(options.AdGuardDnsRuleSetUrl))
        {
            return ValidateOptionsResult.Fail("AdGuard DNS 规则集地址不能为空。");
        }

        if (!Uri.TryCreate(
                options.AdGuardDnsRuleSetUrl,
                UriKind.Absolute,
                out Uri? ruleSetUri)
            || string.IsNullOrWhiteSpace(ruleSetUri.Host))
        {
            return ValidateOptionsResult.Fail("AdGuard DNS 规则集地址必须是绝对 URL。");
        }

        return string.Equals(
            ruleSetUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("AdGuard DNS 规则集地址必须使用 HTTPS。");
    }
}

public sealed class TailscaleOptionsValidator(
    IOptions<SingboxOptions> singboxOptions) : IValidateOptions<TailscaleOptions>
{
    public ValidateOptionsResult Validate(string? name, TailscaleOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.Tag)
            || string.IsNullOrWhiteSpace(options.DnsTag))
        {
            return ValidateOptionsResult.Fail(
                "启用 Tailscale 时 endpoint 和 DNS 标签不能为空。");
        }

        var reservedTags = new[]
        {
            options.DnsTag,
            singboxOptions.Value.MainProxyGroup,
            singboxOptions.Value.Direct
        };
        if (reservedTags.Contains(options.Tag, StringComparer.Ordinal))
        {
            return ValidateOptionsResult.Fail("Tailscale endpoint 标签与其他标签冲突。");
        }

        if (!string.IsNullOrWhiteSpace(options.ControlUrl)
            && (!Uri.TryCreate(options.ControlUrl, UriKind.Absolute, out var controlUri)
                || controlUri.Scheme is not ("http" or "https")))
        {
            return ValidateOptionsResult.Fail("Tailscale 控制平面地址必须是 HTTP(S) URL。");
        }

        return ValidateOptionsResult.Success;
    }
}
