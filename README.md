# SubConvert

SubConvert 是一个将 Clash YAML 配置转换为 sing-box `config.json` 的命令行工具。它会从 GitHub 仓库读取机场配置，按平台生成 sing-box 配置，并支持单文件本地输出或批量回写到仓库。

## 特性

- 从 GitHub 仓库读取 `clashConfigs/*.yaml`
- 支持转换 `trojan`、`vless`、`hysteria2`、`shadowsocks` (ss) 和 `anytls` 节点，包含相关字段与 TLS 映射
- 自动生成地区分组与服务分组
- 自动生成 DNS、路由规则和远程 rule-set
- 按平台生成差异配置，支持 Windows / Android / Linux
- 支持单机场本地导出，也支持批量上传到 GitHub

## 运行环境

- .NET SDK 10.0
- NuGet 依赖：YamlDotNet、Microsoft.Extensions.Configuration 相关包

## 运行方式

```bash
dotnet run
```

也可以通过命令行覆盖配置项，例如：

```bash
dotnet run -- --GitHubOwner=your-name --GitHubToken=your-token
```

程序启动后会先读取环境变量和命令行参数；如果仍缺少 `GitHubOwner` 或 `GitHubToken`，会在终端中交互式提示输入。

## 交互流程

启动后依次选择：

1. 目标平台：Windows / Android / Linux
2. 要处理的机场配置：单个或全部

选择“全部”时，会批量转换并上传到 GitHub；选择单个配置时，会输出到本地文件。

## 输入与输出

- 输入仓库：默认 `SubConfigHub`
- 输入目录：默认 `clashConfigs`
- 批量输出目录：默认 `singboxConfigs/{机场名}/{平台}/config.json`
- 单机场本地输出：默认 `config.json`

默认输入仓库路径示例：`{owner}/SubConfigHub/clashConfigs/*.yaml`

## 配置项

以下配置项可通过环境变量或命令行参数提供：

- `GitHubOwner`
- `GitHubToken`
- `RepoName`
- `SubconfigsFolder`
- `OutputBaseFolder`
- `LocalOutputFile`
- `MainProxyGroup`
- `Direct`

环境变量会使用 `SUBCONVERT_` 前缀，例如：

- `SUBCONVERT_GitHubOwner`
- `SUBCONVERT_GitHubToken`
- `SUBCONVERT_RepoName`

如果未设置这些变量，程序会继续提示输入 `GITHUB_OWNER` 和 `GITHUB_TOKEN`。

## 说明

- Android 平台不会写入 `experimental.clash_api`。
- Windows 的 `external_ui` 为 `ui`，Linux 的 `external_ui` 为 `/etc/sing-box/ui`。
- 批量模式会将结果直接提交到仓库对应路径；单机场模式只写入本地 `config.json`。
