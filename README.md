# BoxForge

BoxForge 是一个 Action-first、无交互的命令行工具，用于将 Clash YAML
配置批量转换为 sing-box 1.14 `config.json`。核心转换与命令行入口保持分离：
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
- 每个 YAML 只解析和转换节点一次，再用于所有目标平台
- 提供稳定退出码，适合 GitHub Actions、其他 CI 和脚本调用

## 运行环境

- .NET SDK 10.0
- sing-box 1.14 或更高版本
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
此时摘要会将已暂存但未生效的项计入“已回滚”，不会误报为生成成功。

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
- `BOXFORGE_ApiSecret`
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

Linux 和 Windows 的 API secret 默认从输入内容与目标平台稳定派生，
不再使用仓库内固定密钥。生产环境建议通过
`BOXFORGE_Singbox__ApiSecret` 显式注入独立高强度密钥，且不要将其写入日志。

启用 Tailscale 后，输出包含一个 `tailscale` endpoint。它复用 sing-box 已有的
系统 VPN/TUN，不创建第二个系统 VPN 接口。Android 上需要 sing-box 1.14 或更高
版本，并在客户端的“工具 > Endpoints”中完成登录。登录状态保存在
`TailscaleStateDirectory`，不会写入 `config.json`。

## 开发验证

```bash
dotnet format BoxForge.slnx --verify-no-changes
dotnet test BoxForge.slnx -c Release
```

仓库中的 CI 同时执行格式检查、Release 测试，并用公开的
`dotnet run -- generate ...` 命令生成测试配置后调用固定版本的
`sing-box check`。CI 不复制核心转换逻辑。

## 说明

- Linux 和 Windows 使用顶层 `services` 中的官方 sing-box API 服务，监听
  `127.0.0.1:9090`，并启用 dashboard；Android 不额外创建 API 监听服务。
- TUN 显式使用 `dns_mode: hijack`；代理节点域名和 Tailscale DNS 不返回
  optimistic 过期缓存，避免地址变化后继续使用旧记录。
- 两台 DNS 并发 `evaluate`；最快出现的有效 A 地址立即胜出。若都没有有效
  地址，才接受任一 `NXDOMAIN`；再否则优先复用第二台已返回的错误响应，
  第二台尚无响应时最后重新 route 它一次。
- Hysteria2 出站使用 `hop_interval: 30s`、`hop_interval_max: 60s` 和
  `bbr_profile: standard`。
- 生成配置包含官方 `$schema`，DNS 缓存容量为 `4096`，启用
  `optimistic` 缓存（`12h`）并通过 `store_dns` 持久化。
- `cache_id` 是有效生成配置的完整 SHA-256，配置选项变更也会刷新缓存身份。
- 远程 rule-set 通过显式的 `http_clients` 使用直连出站下载。
- Windows 的 dashboard 路径为 `ui`。
- Linux 的 dashboard 路径为 `/etc/sing-box/ui`。
