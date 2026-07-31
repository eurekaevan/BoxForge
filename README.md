# BoxForge

BoxForge 是一个将 Clash YAML 配置转换为 sing-box `config.json` 的命令行工具。它会从 GitHub 仓库读取机场配置，按平台生成 sing-box 配置，并支持单文件本地输出或批量回写到仓库。

## 特性

- 从 GitHub 仓库读取 `clashConfigs/*.yaml`
- 支持转换 `trojan`、`vless`、`hysteria2`、`shadowsocks` (ss) 和 `anytls` 节点，包含相关字段与 TLS 映射
- 自动生成地区分组与服务分组
- 自动生成 DNS、路由规则和远程 rule-set
- 按平台生成差异配置，支持 Windows / Android / Linux
- 可选的 sing-box 内置 Tailscale endpoint，手机无需再启动第二个 VPN
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

## 非交互式本地批量生成

`generate` 子命令用于本地目录批量转换，适合 GitHub Actions、其他 CI
环境或脚本调用。它不会读取 GitHub 仓库，也不要求 `GitHubOwner` 或
`GitHubToken`：

```bash
dotnet run -- generate \
  --input-dir clashConfigs \
  --output-dir singboxConfigs \
  --platform all
```

`--platform` 支持 `Android`、`Linux`、`Windows` 和 `all`，不区分大小写。
三个参数都有默认值，因此以下命令等价于上面的完整命令：

```bash
dotnet run -- generate
```

本地模式读取输入目录顶层的全部 `.yaml` 和 `.yml` 文件。使用 `all` 时，
每个输入会生成三个平台的配置：

```text
singboxConfigs/
└── {配置名}/
    ├── Android/config.json
    ├── Linux/config.json
    └── Windows/config.json
```

输入文件和平台均按固定顺序处理，生成内容保持确定性。程序会输出成功、跳过和
失败数量；“跳过”表示生成内容与现有目标文件完全相同。

生成过程先在输出目录的同级临时目录内完成。全部配置成功后才替换输出目录，
同时删除已经不再生成的旧文件；任意转换或配置校验失败时，原输出目录保持不变。

退出码适合直接用于 CI 判断：

- `0`：全部生成成功，或未变化而跳过
- `1`：存在转换、校验、读取或写入失败
- `2`：命令行参数无效
- `130`：任务被取消

例如，只生成 Linux 配置：

```bash
dotnet run -- generate \
  --input-dir ./clashConfigs \
  --output-dir ./artifacts/singboxConfigs \
  --platform Linux
```

开发时可使用以下命令执行完整构建和测试：

```bash
dotnet build BoxForge.slnx
dotnet test BoxForge.slnx --no-build
```

## 交互流程

不带 `generate` 子命令时，程序继续使用原有的交互式 GitHub 流程。

启动后依次选择：

1. 目标平台：Windows / Android / Linux
2. 要处理的机场配置：单个或全部

选择“全部”时，会批量转换并上传到 GitHub；选择单个配置时，会输出到本地文件。

## 输入与输出

- 输入仓库：默认 `BoxVault`
- 输入目录：默认 `clashConfigs`
- 批量输出目录：默认 `singboxConfigs/{机场名}/{平台}/config.json`
- 单机场本地输出：默认 `config.json`

默认输入仓库路径示例：`{owner}/BoxVault/clashConfigs/*.yaml`

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
- `TailscaleEnabled`：是否生成 Tailscale endpoint，默认 `false`
- `TailscaleTag` / `TailscaleDnsTag`
- `TailscaleStateDirectory`：登录状态目录，默认 `tailscale`
- `TailscaleControlUrl`：留空使用官方控制平面，也可填写 Headscale 地址
- `TailscaleHostname`
- `TailscaleAcceptRoutes`：是否接受 tailnet 子网路由，默认 `true`
- `TailscaleExitNode` / `TailscaleExitNodeAllowLanAccess`

程序内部按 `GitHub`、`Output`、`Singbox`、`Tailscale` 分组管理配置，同时继续
兼容以上旧键。也可以使用分组形式，例如：

```bash
dotnet run -- --Tailscale:Enabled=true --Tailscale:Hostname=my-phone
```

环境变量会使用 `BOXFORGE_` 前缀，例如：

- `BOXFORGE_GitHubOwner`
- `BOXFORGE_GitHubToken`
- `BOXFORGE_RepoName`
- `BOXFORGE_TailscaleEnabled=true`

分组配置对应的环境变量使用双下划线，例如
`BOXFORGE_Tailscale__Enabled=true`。新旧形式同时存在时，分组形式优先。

## 在手机上使用 Tailscale

生成配置时启用 Tailscale：

```bash
dotnet run -- --TailscaleEnabled=true
```

生成结果会包含一个 `tailscale` endpoint。它复用 sing-box 已有的系统 VPN/TUN，
不会再创建 Tailscale 系统接口，因此 Android 上不需要同时运行 Tailscale App。
需要使用 sing-box 1.13 或更高版本。

导入并启动配置后，在 sing-box 客户端的“工具 > Endpoints”中完成 Tailscale
交互式登录。登录状态存放在 `TailscaleStateDirectory`，更新订阅配置不会把认证
密钥写进 `config.json` 或上传到 GitHub。

MagicDNS 和 tailnet 节点/子网路由会自动进入 Tailscale；其他流量仍沿用原有代理
规则。如果需要 Headscale，可设置 `TailscaleControlUrl`。如需使用出口节点，
将 `TailscaleExitNode` 设置为节点名称或 Tailscale IP。

如果未设置这些变量，程序会继续提示输入 `BOXFORGE_GITHUB_OWNER` 和
`BOXFORGE_GITHUB_TOKEN`。

## 说明

- Android 平台不会写入 `experimental.clash_api`。
- Windows 的 `external_ui` 为 `ui`，Linux 的 `external_ui` 为 `/etc/sing-box/ui`。
- 批量模式会将结果直接提交到仓库对应路径；单机场模式只写入本地 `config.json`。
