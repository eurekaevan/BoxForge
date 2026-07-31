# BoxForge

BoxForge 是一个 Action-first、无交互的命令行工具，用于将 Clash YAML
配置批量转换为 sing-box `config.json`。核心转换与命令行入口保持分离：
CLI 负责解析参数、调用本地生成服务并返回退出码，转换与文件替换逻辑保留在
应用内部，不写入 GitHub Actions workflow。

## 特性

- 支持转换 `trojan`、`vless`、`hysteria2`、`shadowsocks`（ss）和
  `anytls` 节点
- 自动生成地区分组、服务分组、DNS、路由规则和远程 rule-set
- 支持 Windows、Android 和 Linux 平台差异配置
- 可选 sing-box 内置 Tailscale endpoint，复用已有系统 VPN/TUN
- 输入与平台按固定顺序处理，输出具有确定性
- 全部转换成功后才原子替换输出目录；失败时保留原输出
- 提供稳定退出码，适合 GitHub Actions、其他 CI 和脚本调用

## 运行环境

- .NET SDK 10.0
- NuGet 依赖：YamlDotNet、Microsoft.Extensions.Configuration 相关包

## 非交互式生成

完整命令：

```bash
dotnet run -- generate \
  --input-dir clashConfigs \
  --output-dir singboxConfigs \
  --platform all
```

`--platform` 支持 `Android`、`Linux`、`Windows` 和 `all`，不区分大小写。
三个参数都有默认值，因此也可以运行：

```bash
dotnet run -- generate
```

BoxForge 只接受 `generate` 子命令。不带子命令、使用旧的交互式参数或提供未知
参数时，程序会立即输出用法并以退出码 `2` 结束，不会读取 stdin 或等待人工输入。

本地生成读取输入目录顶层的全部 `.yaml` 和 `.yml` 文件。使用 `all` 时，
每个输入会生成三个平台的配置：

```text
singboxConfigs/
└── {配置名}/
    ├── Android/config.json
    ├── Linux/config.json
    └── Windows/config.json
```

“跳过”表示生成内容与现有目标文件完全相同。生成过程先写入输出目录同级的
暂存目录；全部配置成功后才替换输出目录，并删除不再生成的旧文件。任意转换、
校验、读取或写入失败时，原输出目录保持不变。

只生成 Linux 配置的示例：

```bash
dotnet run -- generate \
  --input-dir ./clashConfigs \
  --output-dir ./artifacts/singboxConfigs \
  --platform Linux
```

## 退出码

- `0`：全部生成成功，或所有内容未变化而跳过
- `1`：存在转换、校验、读取或写入失败
- `2`：命令行参数无效
- `130`：任务被取消

## 配置项

运行时配置通过 `BOXFORGE_` 前缀的环境变量提供。命令行参数只用于选择输入、
输出和目标平台，不用于交互式收集配置。

- `BOXFORGE_MainProxyGroup`
- `BOXFORGE_Direct`
- `BOXFORGE_TailscaleEnabled`
- `BOXFORGE_TailscaleTag`
- `BOXFORGE_TailscaleDnsTag`
- `BOXFORGE_TailscaleStateDirectory`
- `BOXFORGE_TailscaleControlUrl`
- `BOXFORGE_TailscaleHostname`
- `BOXFORGE_TailscaleAcceptRoutes`
- `BOXFORGE_TailscaleExitNode`
- `BOXFORGE_TailscaleExitNodeAllowLanAccess`

也支持分组形式；嵌套键使用双下划线，例如：

```bash
BOXFORGE_Tailscale__Enabled=true \
dotnet run -- generate --platform Android
```

新旧形式同时存在时，分组形式优先。

启用 Tailscale 后，输出包含一个 `tailscale` endpoint。它复用 sing-box 已有的
系统 VPN/TUN，不创建第二个系统 VPN 接口。Android 上需要 sing-box 1.13 或更高
版本，并在客户端的“工具 > Endpoints”中完成登录。登录状态保存在
`TailscaleStateDirectory`，不会写入 `config.json`。

## 开发验证

```bash
dotnet build BoxForge.slnx
dotnet test BoxForge.slnx --no-build
```

仓库中的转换、退出码和原子写入测试应保持通过。GitHub Actions 应调用公开的
`dotnet run -- generate ...` 命令，而不复制核心转换逻辑。

## 说明

- Android 平台不会写入 `experimental.clash_api`。
- Windows 的 `external_ui` 为 `ui`。
- Linux 的 `external_ui` 为 `/etc/sing-box/ui`。
