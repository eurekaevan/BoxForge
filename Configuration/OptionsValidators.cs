using Microsoft.Extensions.Options;

namespace BoxForge.Configuration;

public sealed class GitHubOptionsValidator : IValidateOptions<GitHubOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Repository))
        {
            return ValidateOptionsResult.Fail("GitHub 仓库名称不能为空。");
        }

        return string.IsNullOrWhiteSpace(options.SourceFolder)
            ? ValidateOptionsResult.Fail("GitHub 配置源目录不能为空。")
            : ValidateOptionsResult.Success;
    }
}

public sealed class OutputOptionsValidator : IValidateOptions<OutputOptions>
{
    public ValidateOptionsResult Validate(string? name, OutputOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseFolder))
        {
            return ValidateOptionsResult.Fail("批量输出目录不能为空。");
        }

        return string.IsNullOrWhiteSpace(options.LocalFile)
            ? ValidateOptionsResult.Fail("本地输出文件不能为空。")
            : ValidateOptionsResult.Success;
    }
}

public sealed class SingboxOptionsValidator : IValidateOptions<SingboxOptions>
{
    public ValidateOptionsResult Validate(string? name, SingboxOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MainProxyGroup)
            || string.IsNullOrWhiteSpace(options.Direct))
        {
            return ValidateOptionsResult.Fail("sing-box 主代理组和直连标签不能为空。");
        }

        return string.Equals(
            options.MainProxyGroup,
            options.Direct,
            StringComparison.Ordinal)
            ? ValidateOptionsResult.Fail("sing-box 主代理组和直连标签不能相同。")
            : ValidateOptionsResult.Success;
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
