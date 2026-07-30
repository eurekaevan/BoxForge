using System.Net;
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

    private HttpRequestMessage NewRequest(HttpMethod method, Uri uri)
    {
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("SubConvert/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    public async Task<IReadOnlyList<ConfigSourceItem>> ListAsync(string folderPath)
    {
        using var req = NewRequest(HttpMethod.Get, BuildContentsUri(folderPath));
        using var resp = await Http.SendAsync(req);
        string json = await ReadSuccessfulResponseAsync(resp);
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
        using var req = NewRequest(HttpMethod.Get, BuildContentsUri(path));
        using var resp = await Http.SendAsync(req);
        string json = await ReadSuccessfulResponseAsync(resp);
        var item = JsonSerializer.Deserialize<GitHubContentItem>(json)
                   ?? throw new InvalidOperationException("GitHub API 返回了空响应。");

        if (string.Equals(item.Encoding, "base64", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.Content))
        {
            return DecodeBase64Content(item.Content, path);
        }

        if (string.IsNullOrWhiteSpace(item.Sha))
        {
            throw new InvalidOperationException(
                $"GitHub API 未返回文件 '{path}' 的内容或 blob SHA。");
        }

        return await ReadBlobAsync(item.Sha, path);
    }

    public async Task WriteAsync(ConfigWriteRequest request)
    {
        Uri uri = BuildContentsUri(request.Path);

        string? existingSha = null;
        using (var checkReq = NewRequest(HttpMethod.Get, uri))
        using (var checkResp = await Http.SendAsync(checkReq))
        {
            if (checkResp.IsSuccessStatusCode)
            {
                string existingJson = await checkResp.Content.ReadAsStringAsync();
                existingSha = JsonSerializer.Deserialize<GitHubContentItem>(existingJson)?.Sha;
                if (string.IsNullOrWhiteSpace(existingSha))
                {
                    throw new InvalidOperationException(
                        $"GitHub API 未返回已有文件 '{request.Path}' 的 SHA。");
                }
            }
            else if (checkResp.StatusCode != HttpStatusCode.NotFound)
            {
                await ThrowGitHubApiExceptionAsync(checkResp);
            }
        }

        var body = new Dictionary<string, string?>
        {
            ["message"] = request.ChangeDescription == null
                ? $"chore: update {request.Path}"
                : $"chore: update {request.ChangeDescription}",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Content))
        };
        if (existingSha != null)
            body["sha"] = existingSha;

        string bodyJson = JsonSerializer.Serialize(body);
        using var putReq = NewRequest(HttpMethod.Put, uri);
        putReq.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var putResp = await Http.SendAsync(putReq);
        await ReadSuccessfulResponseAsync(putResp);
    }

    private async Task<string> ReadBlobAsync(string sha, string sourcePath)
    {
        Uri uri = BuildApiUri("git", "blobs", sha);
        using var req = NewRequest(HttpMethod.Get, uri);
        using var resp = await Http.SendAsync(req);
        string json = await ReadSuccessfulResponseAsync(resp);
        var blob = JsonSerializer.Deserialize<GitHubContentItem>(json)
                   ?? throw new InvalidOperationException(
                       $"GitHub API 未返回文件 '{sourcePath}' 的 blob 内容。");

        if (!string.Equals(blob.Encoding, "base64", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(blob.Content))
        {
            throw new InvalidOperationException(
                $"GitHub API 返回了文件 '{sourcePath}' 不支持的 blob 编码。");
        }

        return DecodeBase64Content(blob.Content, sourcePath);
    }

    private Uri BuildContentsUri(string path) =>
        BuildApiUri("contents", SplitAndValidatePath(path));

    private Uri BuildApiUri(string resource, params string[] pathSegments)
    {
        var segments = new[]
        {
            "repos",
            ValidatePathSegment(owner, "GitHub owner"),
            ValidatePathSegment(repository, "GitHub repository"),
            resource
        }.Concat(pathSegments);

        string escapedPath = string.Join("/", segments.Select(Uri.EscapeDataString));
        return new Uri($"https://api.github.com/{escapedPath}");
    }

    private static string[] SplitAndValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("GitHub 文件路径不能为空。", nameof(path));
        }

        string[] segments = [.. path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => ValidatePathSegment(segment, "GitHub path"))];
        if (segments.Length == 0)
        {
            throw new ArgumentException("GitHub 文件路径不能为空。", nameof(path));
        }

        return segments;
    }

    private static string ValidatePathSegment(string segment, string description)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment is "." or "..")
        {
            throw new ArgumentException($"{description} 包含无效路径段。");
        }

        return segment;
    }

    private static string DecodeBase64Content(string content, string path)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(content));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"GitHub API 返回的文件 '{path}' 不是有效的 Base64 内容。",
                ex);
        }
    }

    private static async Task<string> ReadSuccessfulResponseAsync(
        HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowGitHubApiExceptionAsync(response);
        }

        return await response.Content.ReadAsStringAsync();
    }

    private static async Task ThrowGitHubApiExceptionAsync(
        HttpResponseMessage response)
    {
        string responseBody = await response.Content.ReadAsStringAsync();
        string details = string.IsNullOrWhiteSpace(responseBody)
            ? "响应正文为空"
            : responseBody.Length <= 1000
                ? responseBody
                : responseBody[..1000];

        throw new HttpRequestException(
            $"GitHub API 请求失败 ({(int)response.StatusCode} {response.ReasonPhrase}): {details}",
            null,
            response.StatusCode);
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
