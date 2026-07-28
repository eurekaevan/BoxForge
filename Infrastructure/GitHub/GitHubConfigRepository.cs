using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SubConvert.Models;
using SubConvert.Models.GitHub;
using SubConvert.Services;

namespace SubConvert.Infrastructure.GitHub;

public sealed class GitHubConfigRepository(
    string token,
    string owner,
    string repository) : IConfigRepository
{
    private static readonly HttpClient Http = new();

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("SubConvert/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    public async Task<IReadOnlyList<ConfigSourceItem>> ListAsync(string folderPath)
    {
        string url = $"https://api.github.com/repos/{owner}/{repository}/contents/{folderPath}";
        using var req = NewRequest(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(json) ?? [];

        return [.. items
            .Where(i => i.Type == "file" &&
                        i.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Select(item => new ConfigSourceItem(
                Path.GetFileNameWithoutExtension(item.Name),
                item.Path))];
    }

    public async Task<string> ReadAsync(string path)
    {
        string url = $"https://api.github.com/repos/{owner}/{repository}/contents/{path}";
        using var req = NewRequest(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync();
        var item = JsonSerializer.Deserialize<GitHubContentItem>(json)
                   ?? throw new InvalidOperationException("GitHub API 返回了空响应。");

        string base64 = item.Content!.Replace("\n", "").Replace("\r", "");
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    public async Task WriteAsync(
        string path,
        string content,
        string? changeMessage = null)
    {
        string url = $"https://api.github.com/repos/{owner}/{repository}/contents/{path}";

        string? existingSha = null;
        using (var checkReq = NewRequest(HttpMethod.Get, url))
        using (var checkResp = await Http.SendAsync(checkReq))
        {
            if (checkResp.IsSuccessStatusCode)
            {
                string existingJson = await checkResp.Content.ReadAsStringAsync();
                existingSha = JsonSerializer.Deserialize<GitHubContentItem>(existingJson)?.Sha;
            }
        }

        var body = new Dictionary<string, string?>
        {
            ["message"] = changeMessage ?? $"chore: update {path}",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content))
        };
        if (existingSha != null)
            body["sha"] = existingSha;

        string bodyJson = JsonSerializer.Serialize(body);
        using var putReq = NewRequest(HttpMethod.Put, url);
        putReq.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var putResp = await Http.SendAsync(putReq);
        putResp.EnsureSuccessStatusCode();
    }
}

public sealed class GitHubConfigRepositoryFactory
    : IGitHubConfigRepositoryFactory
{
    public IConfigRepository Create(
        string token,
        string owner,
        string repository) =>
        new GitHubConfigRepository(token, owner, repository);
}
