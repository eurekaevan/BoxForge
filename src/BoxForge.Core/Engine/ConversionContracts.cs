using BoxForge.Models;

namespace BoxForge.Engine;

public interface IBoxForgeEngine
{
    Task<ConversionBundle> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ConversionRequest(
    string Name,
    string ClashYaml,
    IReadOnlyList<TargetPlatform> Platforms);

public sealed record ConversionArtifact(
    TargetPlatform Platform,
    string Content,
    string Sha256);

public sealed record ConversionBundle(
    string Name,
    IReadOnlyList<ConversionArtifact> Artifacts);
