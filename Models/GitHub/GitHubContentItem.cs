using System.Text.Json.Serialization;

namespace BoxForge.Models.GitHub;

public record GitHubContentItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("sha")] public string? Sha { get; init; }
    // 单文件 GET 时才存在，内容为 Base64（带换行），列目录时为 null
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("encoding")] public string? Encoding { get; init; }
}
