using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BoxForge.Configuration;
using BoxForge.Models;
using BoxForge.Services;
using BoxForge.Ui;
using BoxForge.Workflows;

namespace BoxForge.App;

public class ConversionOrchestrator(
    IOptions<GitHubOptions> options,
    IGitHubConfigRepositoryFactory githubFactory,
    ILocalConfigDestination localDestination,
    ConversionWorkflow workflow,
    ILogger<ConversionOrchestrator> logger,
    IUserInterface ui)
{
    public async Task RunAsync()
    {
        ui.ShowBanner();

        var githubOptions = options.Value;

        string owner = !string.IsNullOrWhiteSpace(githubOptions.Owner)
            ? githubOptions.Owner
            : ui.RequireInput("BOXFORGE_GITHUB_OWNER", "请输入 GitHub 用户名 (仓库所有者): ");

        string token = !string.IsNullOrWhiteSpace(githubOptions.Token)
            ? githubOptions.Token
            : ui.RequireInput("BOXFORGE_GITHUB_TOKEN", "请输入 GitHub Personal Access Token: ", secret: true);

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(token))
        {
            logger.LogError("✗ 用户名或 Token 不能为空，程序退出。");
            return;
        }

        var github = githubFactory.Create(token, owner, githubOptions.Repository);

        logger.LogInformation(
            "● 正在读取 {Owner}/{RepoName}/{Folder}...",
            owner,
            githubOptions.Repository,
            githubOptions.SourceFolder);

        IReadOnlyList<ConfigSourceItem> files;
        try
        {
            files = await github.ListAsync(githubOptions.SourceFolder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "✗ 获取文件列表失败：{Message}", ex.Message);
            return;
        }

        if (files.Count == 0)
        {
            logger.LogError("✗ {Folder}/ 文件夹内未找到任何 YAML 文件。", githubOptions.SourceFolder);
            return;
        }

        TargetPlatform platform = ui.SelectPlatform();
        int selection = ui.SelectAirport(files);

        if (selection < 1)
        {
            logger.LogError("✗ 无效选项，程序退出。");
            return;
        }

        bool allMode = selection == files.Count + 1;
        if (allMode)
        {
            await workflow.ProcessBatchAsync(
                github,
                github,
                files,
                platform);
        }
        else
        {
            await workflow.ProcessSingleAsync(
                github,
                localDestination,
                files[selection - 1],
                platform);
        }
    }
}
