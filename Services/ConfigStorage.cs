using SubConvert.Models;

namespace SubConvert.Services;

public interface IConfigSource
{
    Task<IReadOnlyList<ConfigSourceItem>> ListAsync(string folderPath);
    Task<string> ReadAsync(string path);
}

public interface IConfigDestination
{
    Task WriteAsync(
        string path,
        string content,
        string? changeMessage = null);
}

public interface IConfigRepository : IConfigSource, IConfigDestination;

public interface IGitHubConfigRepositoryFactory
{
    IConfigRepository Create(string token, string owner, string repository);
}
