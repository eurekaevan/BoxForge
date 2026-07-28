using SubConvert.Services;

namespace SubConvert.Infrastructure.FileSystem;

public sealed class LocalFileDestination : IConfigDestination
{
    public Task WriteAsync(
        string path,
        string content,
        string? changeMessage = null) =>
        File.WriteAllTextAsync(path, content);
}
