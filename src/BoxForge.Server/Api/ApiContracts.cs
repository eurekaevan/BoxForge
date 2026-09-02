namespace BoxForge.Server.Api;

internal sealed record ConvertApiRequest(
    string? Name,
    string? Yaml,
    IReadOnlyList<string?>? Platforms);

internal sealed record ConvertApiResponse(
    string Name,
    IReadOnlyList<ConvertApiArtifact> Artifacts);

internal sealed record ConvertApiArtifact(
    string Platform,
    string Path,
    string Sha256,
    string Content);

internal sealed record HealthResponse(string Status);
