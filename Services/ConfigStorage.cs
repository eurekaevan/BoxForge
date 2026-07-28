using SubConvert.Models;

namespace SubConvert.Services;

public interface IConfigSource
{
    Task<IReadOnlyList<ConfigSourceItem>> ListAsync(string folderPath);
    Task<string> ReadAsync(string path);
}

public interface IConfigDestination
{
    Task WriteAsync(ConfigWriteRequest request);
}

public interface IConfigRepository : IConfigSource, IConfigDestination;

public interface ILocalConfigDestination : IConfigDestination;

public interface IGitHubConfigRepositoryFactory
{
    IConfigRepository Create(string token, string owner, string repository);
}
