using BoxForge.Models;

namespace BoxForge.Workflows;

public sealed record LocalGenerationRequest(
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<TargetPlatform> Platforms);

public sealed record LocalGenerationSummary(
    int Succeeded,
    int Skipped,
    int Failed,
    int Discarded = 0)
{
    public int Total => Succeeded + Skipped + Failed + Discarded;
    public bool HasFailures => Failed > 0 || Discarded > 0;
}

public interface ILocalGenerationWorkflow
{
    Task<LocalGenerationSummary> GenerateAsync(
        LocalGenerationRequest request,
        CancellationToken cancellationToken = default);
}
