namespace BoxForge.Configuration;

public sealed class GitHubOptions
{
    public string Owner { get; set; } = "";
    public string Token { get; set; } = "";
    public string Repository { get; set; } = "BoxVault";
    public string SourceFolder { get; set; } = "clashConfigs";
}
