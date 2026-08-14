# BoxForge

BoxForge 是一个无交互的命令行工具，用于将 Clash YAML
配置批量转换为 sing-box 1.14 `config.json`。核心转换与命令行入口保持分离：
CLI 负责解析参数、调用本地生成服务并返回退出码，转换与文件替换逻辑保留在
应用内部。

## 特性

- 支持转换 `trojan`、`vless`、`hysteria2`、`shadowsocks`（ss）和
  `anytls` 节点
- 自动生成地区分组、服务分组、DNS、路由规则和远程 rule-set
- 支持 Windows、Android 和 Linux 平台差异配置
- 可选 sing-box 内置 Tailscale endpoint，复用已有系统 VPN/TUN
- 输入与平台按固定顺序处理，输出具有确定性
- 全部转换成功后才原子替换输出目录；失败时保留原输出
- 每个 YAML 只解析和转换节点一次，再用于所有目标平台
- 提供稳定退出码，适合自动化脚本调用

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
系统 VPN/TUN，不创建第二个系统 VPN 接口。Android 上需要 sing-box 1.14 或更高
版本，并在客户端的“工具 > Endpoints”中完成登录。登录状态保存在
`TailscaleStateDirectory`，不会写入 `config.json`。

## 开发验证

```bash
dotnet format BoxForge.slnx --verify-no-changes
dotnet build BoxForge.slnx --warnaserror
dotnet test BoxForge.slnx --no-build
```

## 说明

- 内置校验只补充 `sing-box check` 未覆盖的生成器约束；JSON 结构、类型和
  sing-box 能识别的引用错误由后续 `sing-box check` 负责。
- TUN 显式使用 `dns_mode: hijack`；代理节点域名和 Tailscale DNS 不返回
  optimistic 过期缓存，避免地址变化后继续使用旧记录。
- 协议嗅探不限端口，但 TCP 仅启用 HTTP/TLS，UDP 仅启用 QUIC；固定
  STUN 拒绝位于嗅探之前。命中国内直连规则的流量允许使用 UDP/443；未命中
  国内规则的国外或最终代理 IPv4 UDP/443 会快速拒绝，以促使 QUIC 回退 TCP。
- AI 与 `geosite-google` 均默认使用美国组，AI 分流优先于 Google，二者又优先于
  所有引用 `geosite-cn` 的规则；其 UDP/443 会先拒绝并回退 TCP。Google 域名
  也会优先使用远程 DNS，不会先命中国内 DNS。
- 国内域名允许 A/AAAA，并将命中 `geosite-cn`、`geosite-category-pt` 或
  `geoip-cn` 的公网 IPv6 直连；其他 AAAA 返回空结果，未命中的公网 IPv6
  仍会拒绝，避免绕过代理策略。全局 DNS 使用 `prefer_ipv4`，保持 IPv4 优先
  并允许国内规则返回 AAAA。
- 两台 DNS 并发 `evaluate`；最快出现的有效地址立即胜出。若都没有有效地址，
  才接受任一 `NXDOMAIN`；再否则优先复用第二台已返回的错误响应，第二台尚无
  响应时最后重新 route 它一次。
- Hysteria2 出站使用 `hop_interval: 30s`、`hop_interval_max: 60s` 和
  `bbr_profile: standard`。
- 生成配置包含官方 `$schema`，DNS 缓存容量为 `4096`，启用
  `optimistic` 缓存（`3d`）并通过 `store_dns` 持久化。
- `cache_id` 是 YAML `proxies` 列表的规范化 SHA-256；只要核心代理列表相同，
  不同平台或其他配置项就会复用同一缓存身份。
- 远程 rule-set 通过 `rule-set-direct` 使用直连出站下载。广告过滤同时使用
  anti-AD 的 `anti-ad-sing-box.srs` 与 SagerNet 的
  `geosite-category-ads-all.srs`，不再依赖自建 AdGuard DNS Filter SRS。
