using System.IO.Compression;
using System.Text;
using BoxForge.Engine;

namespace BoxForge.Server.Api;

internal static class ConversionZipBuilder
{
    public static async Task<byte[]> BuildAsync(
        ConversionBundle bundle,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (ConversionArtifact artifact in bundle.Artifacts)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    $"{bundle.Name}/{artifact.Platform}/config.json",
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
}
