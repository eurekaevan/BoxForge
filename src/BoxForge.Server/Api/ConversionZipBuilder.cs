using System.IO.Compression;
using System.Text;
using BoxForge.Engine;
using BoxForge.Models;

namespace BoxForge.Server.Api;

internal static class ConversionZipBuilder
{
    public static async Task<byte[]> BuildAsync(
        string name,
        IReadOnlyList<TargetPlatform> requestedPlatforms,
        ConversionBundle bundle,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversionArtifact> artifacts =
        [
            .. bundle.Artifacts.OrderBy(artifact =>
                GetPlatformOrder(artifact.Platform))
        ];
        EnsureCompleteBundle(artifacts, requestedPlatforms);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (ConversionArtifact artifact in artifacts)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    $"{name}/{artifact.Platform}/config.json",
                    CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(
                    1980,
                    1,
                    1,
                    0,
                    0,
                    0,
                    TimeSpan.Zero);

                byte[] content = Encoding.UTF8.GetBytes(artifact.Content);
                await using Stream destination = entry.Open();
                await destination.WriteAsync(content, cancellationToken);
            }
        }

        return output.ToArray();
    }

    private static void EnsureCompleteBundle(
        IReadOnlyList<ConversionArtifact> artifacts,
        IReadOnlyList<TargetPlatform> requestedPlatforms)
    {
        var expectedPlatforms = requestedPlatforms.ToHashSet();
        var actualPlatforms = artifacts
            .Select(artifact => artifact.Platform)
            .ToHashSet();
        if (expectedPlatforms.Count != requestedPlatforms.Count
            || artifacts.Count != expectedPlatforms.Count
            || actualPlatforms.Count != artifacts.Count
            || !expectedPlatforms.SetEquals(actualPlatforms))
        {
            throw new InvalidOperationException(
                "The conversion bundle does not match the request.");
        }
    }

    private static int GetPlatformOrder(TargetPlatform platform) =>
        platform switch
        {
            TargetPlatform.Android => 0,
            TargetPlatform.Linux => 1,
            TargetPlatform.Windows => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(platform),
                platform,
                "Unknown target platform.")
        };
}
