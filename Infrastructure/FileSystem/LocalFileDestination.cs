using BoxForge.Models;
using BoxForge.Services;

namespace BoxForge.Infrastructure.FileSystem;

public sealed class LocalFileDestination : ILocalConfigDestination
{
    public Task WriteAsync(ConfigWriteRequest request) =>
        File.WriteAllTextAsync(request.Path, request.Content);
}
