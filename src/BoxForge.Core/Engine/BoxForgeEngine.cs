using BoxForge.Exceptions;
using BoxForge.Builders;
using BoxForge.Builders.Components;
using BoxForge.Configuration;
using BoxForge.Models.Intermediate;
using BoxForge.Parsers;
using BoxForge.Models;
using BoxForge.Services;
using YamlDotNet.Core;
using System.Text.RegularExpressions;

namespace BoxForge.Engine;

internal sealed class BoxForgeEngine(
    IClashParser clashParser,
    ISingboxConfigBuilder configBuilder,
    ISingboxConfigValidator configValidator,
    IConfigSerializer configSerializer,
    BoxForgeProfile profile) : IBoxForgeEngine
{
    private static readonly IReadOnlyList<TargetPlatform> PlatformOrder =
    [
        TargetPlatform.Android,
        TargetPlatform.Linux,
        TargetPlatform.Windows
    ];

    public Task<ConversionBundle> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var requestedPlatforms = request.Platforms.ToHashSet();
        var profiles = PlatformOrder.Where(requestedPlatforms.Contains)
            .ToDictionary(platform => platform, platform => profile.Resolve(platform, request.Profile));
        NodeCatalog nodes;
        try
        {
            var clash = clashParser.Parse(request.ClashYaml)
                ?? throw new NodeParseException("YAML 解析失败，请检查文件内容。");
            nodes = NodeCatalogBuilder.Build(clash);
        }
        catch (Exception ex) when (ex is NodeParseException or YamlException)
        {
            throw new BoxForgeConversionException(
                "Clash YAML 转换失败。",
                ex);
        }
        var artifacts = new List<ConversionArtifact>(requestedPlatforms.Count);

        foreach (TargetPlatform platform in PlatformOrder)
        {
            if (!requestedPlatforms.Contains(platform))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ConfigurationPlan plan = ConfigurationPlanner.Plan(nodes, profiles[platform]);
                string cacheId = configSerializer.GetContentHash(request.Name + platform.ToString().ToLowerInvariant());
                var config = configBuilder.Build(plan, cacheId);
                configValidator.Validate(config, profiles[platform].Settings.AddressFamily);
                string content = configSerializer.Serialize(config);
                artifacts.Add(new ConversionArtifact(
                    platform,
                    content,
                    configSerializer.GetContentHash(content)));
            }
            // Preserve the existing error contract for a conflict on the first requested platform.
            // Name binding belongs to platform planning even though this historical case is a conversion error.
            catch (NodeParseException ex) when (platform == profiles.First().Key)
            {
                throw new BoxForgeConversionException("Clash YAML 转换失败。", ex);
            }
            catch (Exception ex) when (ex is ConfigValidationException or RegexMatchTimeoutException or NodeParseException)
            {
                throw new BoxForgePlatformConversionException(platform, ex);
            }
        }

        return Task.FromResult(new ConversionBundle(
            request.Name,
            artifacts.ToArray()));
    }

    private static void ValidateRequest(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException(
                "配置名称不能为空。",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ClashYaml))
        {
            throw new ArgumentException(
                "Clash YAML 不能为空。",
                nameof(request));
        }

        if (request.Platforms is not { Count: > 0 })
        {
            throw new ArgumentException(
                "目标平台不能为空。",
                nameof(request));
        }

        if (request.Platforms.Any(platform => !Enum.IsDefined(platform)))
        {
            throw new ArgumentException(
                "目标平台必须是有效值。",
                nameof(request));
        }

        if (request.Platforms.Distinct().Count() != request.Platforms.Count)
        {
            throw new ArgumentException(
                "目标平台不能重复。",
                nameof(request));
        }
    }
}
