using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Services;

namespace BoxForge.Engine;

public sealed class BoxForgeEngine(
    ConversionService conversionService,
    IConfigSerializer configSerializer) : IBoxForgeEngine
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

        PreparedConversion prepared;
        try
        {
            prepared = conversionService.Prepare(
                request.ClashYaml,
                strictNodeValidation: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BoxForgeConversionException(
                "Clash YAML 转换失败。",
                ex);
        }
        var requestedPlatforms = request.Platforms.ToHashSet();
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
                string content = conversionService.Convert(prepared, platform);
                artifacts.Add(new ConversionArtifact(
                    platform,
                    content,
                    configSerializer.GetContentHash(content)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
