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
- 使用 DB-IP City Lite 与 IP2Location.io 双来源为节点 tag 追加英文城市名
- 输入与平台按固定顺序处理，输出具有确定性
- 全部转换成功后才原子替换输出目录；失败时保留原输出
- 每个 YAML 只解析和转换节点一次，再用于所有目标平台
- 提供稳定退出码，适合自动化脚本调用

## 运行环境

- .NET SDK 10.0
- sing-box 1.14 或更高版本
- NuGet 依赖：YamlDotNet、用于读取 DB-IP MMDB 的 MaxMind.GeoIP2、
  Microsoft.Extensions.Configuration 相关包

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
- `BOXFORGE_NodeEnrichment__Enabled`（默认 `true`）
- `BOXFORGE_NodeEnrichment__Mode`（默认 `Exit`）
- `BOXFORGE_NodeEnrichment__Ip2LocationApiKey`（可选）
- `BOXFORGE_NodeEnrichment__DbIpDatabaseUrl`（可选）
- `BOXFORGE_NodeEnrichment__SingBoxPath`（默认 `sing-box`）

也支持分组形式；嵌套键使用双下划线，例如：

```bash
BOXFORGE_Tailscale__Enabled=true \
dotnet run -- generate --platform Android
```

新旧形式同时存在时，分组形式优先。

节点城市标注默认开启，使用真实出口 IP 检测。完整配置示例：

```bash
BOXFORGE_NodeEnrichment__Enabled=true \
BOXFORGE_NodeEnrichment__Mode=Exit \
BOXFORGE_NodeEnrichment__Ip2LocationApiKey=... \
BOXFORGE_NodeEnrichment__SingBoxPath=/path/to/sagernet-sing-box \
dotnet run -- generate
```

`SingBoxPath` 必须指向能执行 `sing-box version` 和 `sing-box run -c ...` 的
SagerNet 命令行 core。某些桌面客户端也安装了名为 `sing-box` 的 Electron
启动器，它不能用于出口检测。BoxForge 会在首个节点前验证可执行文件；
路径错误时只输出一条 warning，并保留所有原 tag。

如需关闭：

```bash
BOXFORGE_NodeEnrichment__Enabled=false dotnet run -- generate
```

`Exit` 模式在节点转换为 outbound 后顺序测试每个节点。BoxForge
为当前节点生成包含本地 `mixed` inbound、该节点 outbound 和默认
路由的临时 sing-box 配置，通过本地 SOCKS 代理访问
`https://api.ipify.org` 取得真实出口 IP。IPv4 端点失败时，会在同一
10 秒节点检测窗口内尝试 `https://api64.ipify.org`。两次都失败时，
日志只记录 DNS、连接、TLS、代理隧道或 HTTP 状态等安全错误分类，不记录
原始异常；sing-box 的原始输出同样只会映射为白名单错误分类，不记录节点
地址或凭据。域名型节点会使用 Clash `dns.nameserver-policy` 中匹配的 HTTPS
DNS；没有匹配项时使用内置节点 DNS。单节点出口检测超时为 10 秒；
每次检测结束都会停止 sing-box 并删除临时配置，失败或取消时也会执行
同样的清理。临时 outbound 保留节点原始 `server`，域名解析和地址选择由
sing-box 完成；为此域名型节点的临时配置还会包含最小 DNS 段。仅 ipify
返回的出口 IP 参与城市判断。

启用时，BoxForge 从以下默认地址下载 DB-IP City Lite MMDB：

```text
https://cdn.jsdelivr.net/npm/dbip-city-lite/dbip-city-lite.mmdb.gz
```

下载内容通过 `GZipStream` 解压到系统临时目录。一次 `generate` 运行只下载、
解压并打开数据库一次，所有输入与平台复用同一个 `DatabaseReader`；进程结束时
释放 Reader 并清理临时目录。可覆盖下载地址：

```bash
BOXFORGE_NodeEnrichment__DbIpDatabaseUrl=https://example.com/dbip-city-lite.mmdb.gz \
dotnet run -- generate
```

真实出口 IP 会分别查询 DB-IP City Lite 和 IP2Location.io：

```text
https://api.ip2location.io/?ip={IP}
```

`Ip2LocationApiKey` 为空时不发送认证请求头，使用 IP2Location.io 每日 1000 次的
无 Key 接口。配置 Key 时通过 `Authorization: Bearer` 请求头发送，不放入 URL；
API Key 不会写入日志或异常。相同出口 IP 在一次 `generate` 中只查询
一次城市，但不同节点仍会分别启动 sing-box 并检测各自出口。

两个来源均取英文城市名并 `Trim`；DB-IP 城市名还会移除末尾括号内容。
两个来源结果相同时 tag 为 `原tag>City`；不同时固定为
`原tag>DbIpCity\Ip2LocationCity`；只有一个
来源有结果时使用该结果，两者都没有时保留原 tag。节点 `server` 不会改变，最终
tag 会在生成分组前同步写入 outbound 和节点名称目录，因此 selector 及由该目录
构建的 urltest 都不会保留旧 tag（当前 profile 未生成 urltest）。出口检测、下载、
解压、数据库或 API 任一环节
失败只输出 warning，不会阻断其他节点或导致配置生成失败。

免费 IP2Location.io 查询的署名：IP geolocation by IP2Location.io

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
  STUN 拒绝位于嗅探之前。命中国内直连规则的 UDP/443 会快速拒绝以促使
  QUIC 回退 TCP；未命中的国外或最终代理流量继续使用 UDP/443。
- 两台 DNS 并发 `evaluate`；最快出现的有效 A 地址立即胜出。若都没有有效
  地址，才接受任一 `NXDOMAIN`；再否则优先复用第二台已返回的错误响应，
  第二台尚无响应时最后重新 route 它一次。
- Hysteria2 出站使用 `hop_interval: 30s`、`hop_interval_max: 60s` 和
  `bbr_profile: standard`。
- 生成配置包含官方 `$schema`，DNS 缓存容量为 `4096`，启用
  `optimistic` 缓存（`3d`）并通过 `store_dns` 持久化。
- `cache_id` 是 YAML `proxies` 列表的规范化 SHA-256；只要核心代理列表相同，
  不同平台或其他配置项就会复用同一缓存身份。
- 远程 rule-set 通过显式的 `http_clients` 使用直连出站下载。
