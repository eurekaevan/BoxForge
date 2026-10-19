# 配置参考

BoxForge 提供两个 Tailscale 运行时设置，并提供一个全平台 sing-box API 开关。
使用分组键时，环境变量中的 `__` 对应配置路径中的 `:`。
CLI 和 HTTP Server 都读取带 `BOXFORGE_` 前缀的环境变量，使用相同的开关和默认值；
修改 Server 环境变量后需要重启进程，新值才会用于转换。

## 配置表

| 推荐环境变量 | 兼容键 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `BOXFORGE_Tailscale__Enabled` | `BOXFORGE_TailscaleEnabled` | `true` | 是否在 Linux 和 Windows 生成 Tailscale endpoint |
| `BOXFORGE_Tailscale__AndroidEnabled` | `BOXFORGE_TailscaleAndroidEnabled` | `true` | 是否在 Android 生成 Tailscale endpoint |
| `BOXFORGE_SingboxApi__Enabled` | 无 | `false` | 是否生成仅监听本机的 sing-box API 与 Dashboard |

同时设置分组键和兼容键时，分组键优先；建议只使用表中的推荐变量。

`Enabled` 和 `AndroidEnabled` 只接受 `true` 或 `false`（不区分大小写）。
无法解析的值会使生成失败。两者默认均为 `true`，但 Android 不继承桌面端的
`Enabled`：将其中一个设为 `false` 只关闭对应平台的 Tailscale endpoint、DNS
和路由，不改变另一个开关。

`SingboxApi:Enabled` 同样只接受 `true` 或 `false`。启用后，三个目标平台都会生成
`type: api` service，固定监听 `127.0.0.1:9090`。Dashboard 文件由目标机器上的
sing-box 下载到工作目录下的 `dashboard`，下载使用独立的
`http-dashboard-direct` HTTP client；远程 rule-set 则通过
`http-ruleset-proxy` 经地区 AUTO（若无则首个节点）下载。CORS 只允许该端口的
`127.0.0.1` 和 `localhost` origin，且不允许浏览器私网跨域访问。

API 配置不开放监听地址、端口或远程访问，也不生成共享 secret。尽管只监听
loopback，同机进程仍可访问该控制面；不需要 Dashboard、远程控制或 Tailscale
交互管理时应保持关闭。

## 地区 profile

使用 UTF-8 JSON 文件，只写需要覆盖的地区字段。例如 `profile.json`：

```json
{
  "version": 1,
  "regions": [
    { "id": "us", "display_name": "🇺🇸 USA" },
    { "id": "tw", "display_name": "🇹🇼 TW", "pattern": "🇹🇼|台湾|台灣|taiwan|(?<![a-z])tw[0-9]*(?![a-z])" }
  ]
}
```

```bash
dotnet run --project src/BoxForge.Cli -- generate --profile profile.json
dotnet run --project src/BoxForge.Cli -- profile show --resolved --profile profile.json
dotnet run --project src/BoxForge.Server -- --profile profile.json
```

CLI 和 Server 也可以使用 `BOXFORGE_Profile=profile.json`，或宿主配置键 `Profile`。
显式 `--profile` 高于环境变量；没有指定文件时使用内置默认值。相对路径以进程工作
目录为基准；Server 启动时加载一次，修改文件后需重启。HTTP 请求不能指定文件路径。

默认地区 ID 为 `us`、`jp`、`hk`、`sg`。ID 区分大小写，接受以小写字母开头的
1–32 位小写字母、数字或连字符。按 ID 合并：已有地区保留原位置，缺省字段继承；
新地区按文件顺序追加，必须同时提供 `display_name` 和 `pattern`。`regions: []`
保留全部默认地区。本阶段不提供删除、停用或排序入口；服务偏好地区通过下节的
`services` 覆盖修改。

`pattern` 是完整替换的 .NET 正则表达式，忽略大小写且不依赖主机文化；JSON 中的
反斜杠需写成 `\\`。单次匹配最多 250ms，超时使整次转换失败。地区分组沿用原规则：
匹配到至少两个节点才生成 selector 和 AUTO，单个节点仍可作为现有服务的默认目标。
未修改的服务通过 ID 引用地区，修改 `us` 的显示名称后引用会跟随新名称。

严格拒绝未知键、重复 JSON 键、重复 ID、显式 `null`、无效正则和缺失的新地区字段。
地区显示名不能含控制字符、首尾空白，生成的 selector/AUTO tag 不能彼此冲突或
占用固定保留 tag；订阅节点也不能使用有效 profile 中的地区 tag。
覆盖文件当前只接受顶层 `version`、`regions`、`services`、`rule_sets`、`resolvers`，地区内只接受 `id`、
`display_name`、`pattern`。服务、来源和 resolver 字段见下节；平台和隐私
覆盖均会作为未知键拒绝。

`profile show --resolved` 将完整有效默认数据、合并后的地区、服务、来源和 resolver，以及实际环境开关
（`runtime`）作为纯 JSON 输出到 stdout，可重定向留档。不带 `--profile` 时也能查看
默认配置或环境变量选定的配置。该完整输出用于检查和追溯，不能直接作为覆盖文件
重新加载；覆盖文件只记录差异。本阶段没有 `profile diff` 或 JSON Schema。

## 服务 profile

在同一份文件中添加 `services`，按稳定 ID 覆盖服务。例如新增 TW 地区，将 AI
偏好地区改为 TW，并添加优先于内置 Games 的服务：

```json
{
  "version": 1,
  "regions": [
    { "id": "tw", "display_name": "🇹🇼 TW", "pattern": "台湾|台灣|taiwan|TW[0-9]+" }
  ],
  "services": [
    { "id": "ai", "default_region": "tw" },
    {
      "id": "custom-games",
      "name": "Custom Games",
      "rule_sets": ["games"],
      "default_region": "tw",
      "precedes_domestic_routes": true
    }
  ]
}
```

默认服务 ID 依次为 `ai`、`google`、`spotify`、`games`、`microsoft`，ID 格式与
地区一致。已有服务原位合并，省略字段继承默认值；新服务按文件顺序追加，必须
提供 `name` 和 `rule_sets`。新服务省略 `default_region` 时默认选择主代理组，
省略 `precedes_domestic_routes` 时为 `false`。`services: []` 保留全部默认服务。

只接受 `id`、`name`、`rule_sets`、`default_region`、`precedes_domestic_routes`。
`rule_sets` 整体替换，不能是空数组，元素必须唯一并引用现有 rule-set tag；本阶段
不会通过服务条目新增或修改下载来源；来源单独通过下节覆盖。`default_region` 必须引用合并后的地区 ID，可在同一文件
中新增地区。`name` 是实际 selector tag，必须与所有有效地区、服务及固定保留 tag
互不冲突；订阅节点同样不能占用它。修改服务名称后，路由引用跟随新名称。
未知键、重复键、重复 ID、悬空引用和显式 `null` 均拒绝。

`precedes_domestic_routes` 只接受布尔值：`true` 使用既有 `PriorityServices`
阶段，在国内 UDP/443 规则之前；`false` 使用 `OtherServices` 阶段，在国内
UDP/443 规则之后、普通国内直连之前。两者都在后置 IPv6 守卫之后；前置 IPv6、
广告及 STUN 拒绝规则继续优先执行。不能通过 profile 指定其他阶段、修改规则顺序
或绕过守卫。DNS 服务域名池随有效服务的 `rule_sets` 同步更新。

路由仍是首次匹配：同一阶段内默认服务保持原顺序，新服务追加。允许不同服务引用
相同 rule-set；新增同阶段服务不能抢占已有服务的匹配。示例使用 `true` 将自定义
Games 放入优先阶段，先于仍位于普通阶段的内置 Games；如果改为 `false`，内置
Games 会先匹配。远端 rule-set 内容本身也可能重叠，服务 ID 不提供额外排序能力。
`profile show --resolved` 会展示最终服务顺序和全部有效字段。

## rule-set 来源 profile

`rule_sets` 按已有 tag 覆盖 URL 或更新间隔，不会增加、删除或改名 rule-set：

```json
{
  "version": 1,
  "rule_sets": [
    { "tag": "ai", "url": "https://rules.example.net/{tag}.srs", "update_interval": "12h" },
    { "tag": "google", "update_interval": "1d12h" }
  ]
}
```

`rules.example.net` 是示例地址，使用时替换为实际托管二进制 SRS 的来源。
每个条目只接受 `tag`、`url`、`update_interval`。`tag` 必填且不能重复，只能是
`ai`、`spotify`、`games`、`cn`、`cnip`、`ads`、`google`、`microsoft` 中的一个。
省略字段继承该 tag 的默认来源；`rule_sets: []`、仅指定 tag 或填写相同默认值
不会拆分原分组，也不改变生成结果。未知键、未知 tag、重复键和显式 `null` 均拒绝。

URL 只接受绝对 HTTPS 地址，不能含用户信息、片段或未编码的空白/控制字符。
只识别字面量 `{tag}`，且它只能位于路径或查询部分；不允许其他或不完整占位符。
拆出的单 tag 会展开 URL 中所有 `{tag}`，例如示例的 `ai` 生成
`https://rules.example.net/ai.srs`。只改间隔时也继承并展开原 URL。

内置 `ai/spotify/games/cn/cnip` 共用一个 `{tag}` 批量来源。覆盖其中一个成员时，
仅将它拆出为独立来源，未覆盖成员保留原 URL 和间隔；原组只剩一个成员时展开其
URL，全部覆盖时不留下空组。处理顺序固定为原来源顺序，各组中保留成员在前、
拆出成员按原 tag 顺序排列，不受覆盖文件条目顺序影响。服务和 DNS/路由仍引用
原 tag，不随 URL 更换而变化。

`update_interval` 必须是带单位的正 duration，最长 128 字符，不超过
`9223372036854775807ns`。支持 `ns/us/µs/μs/ms/s/m/h/d`、小数、组合和前导 `+`，
其中 `d` 为 24 小时；例如 `.5d`、`1d12h`。不接受零、负值、裸数字或 `w/M`。
包含小数的值按精确数值截断至纳秒，并规范化为整数 `ns` 输出，例如 `.5d` 为
`43200000000000ns`，避免核心浮点舍入导致边界值溢出；普通整数单位表示保持原样。
语法依据[固定核心依赖的 duration 解析器](https://github.com/SagerNet/sing/blob/4ca3bebe0b8e/common/json/badoption/internal/my_time/format.go)；AnyTLS 的 Go duration 字段仍不接受 `d`。

生成格式固定为 remote/binary，下载仍使用 `http-ruleset-proxy`，不开放 `format`、
`type`、本地路径、HTTP client 或规则内容注入。路由阶段顺序和 IPv6 守卫保持不变。
`profile show --resolved` 展示合并、拆组后的实际来源（`tags` 数组）；完整 resolved
输出仍用于留档，不能直接当作只接受差异字段的覆盖文件重新加载。
生成过程不会下载来源文件，也不证明远端内容是有效 SRS 或来源实际可用。

## resolver profile

`resolvers` 按固定 ID 覆盖 DoH 的 IPv4 地址和 TLS server name：

```json
{
  "version": 1,
  "resolvers": [
    { "id": "alidns", "server": "192.0.2.53", "server_name": "dns.example.net" }
  ]
}
```

这里的地址和名称只用于演示，使用时替换为实际可用且证书匹配的 DoH 端点。
条目只接受 `id`、`server`、`server_name`，ID 必填且不能重复。省略字段继承默认值，
条目顺序不改变主备顺序；空数组、只指定 ID 或相同默认值均不改变输出。
ID 标识固定角色，即使替换了供应商也保留原 ID 和生成 tag：

| 固定 ID | 固定 tag | 固定角色 |
| --- | --- | --- |
| `alidns` | `dns-direct-alidns` | 国内主解析器；控制面引导解析器，直连 |
| `tencent` | `dns-direct-tencent` | 国内备用解析器，直连 |
| `cloudflare` | `dns-proxy-cloudflare` | GLOBAL/服务主解析器及 DNS final，经主代理组 |
| `google` | `dns-proxy-google` | GLOBAL/服务备用解析器，经主代理组 |

`server` 只接受规范的单播 IPv4 字面量，不接受域名、IPv6、URL、端口、CIDR、
数字/八进制等非规范写法、`0.*` 或首段大于等于 224 的地址。允许私网及 loopback
地址，以支持用户已有的本地 HTTPS resolver；这不代表该端点已具备有效证书。
`server_name` 必须是 ASCII DNS 名称（国际域名使用 punycode），总长 1–253 字符，
每个 label 长 1–63 字符，只能包含字母、数字及内部连字符。拒绝 IP、URL、端口、
通配符、空 label、尾点、首尾连字符、空白或控制字符。

更换 `alidns.server` 后，sniff 前的 bootstrap 直连 CIDR 同步更新；Linux 的
bypass、bridge 和 DIRECT 展开规则也使用新地址。代理节点和 Tailscale 继承的
`route.default_domain_resolver`、rule-set/Dashboard HTTP client 的显式 resolver
都继续引用同一个 `dns-direct-alidns` tag，因此实际使用覆盖后的端点。TCP 连接使用
IP 字面量，TLS 只使用 `server_name` 校验证书，不需要先解析该名称。

TLS 始终启用，端口与 DoH 路径沿用核心已有默认值，不开放系统 DNS、明文回退、
`insecure`、任意 detour、主备关系或平台 DNS 模式覆盖。未知 ID、未知/重复键、
非法值和显式 `null` 均拒绝。Android 顺序回退、桌面并行竞速及两道 IPv6 守卫
保持不变。`profile show --resolved` 展示最终四个端点；生成及核心配置检查不能
证明远端可达、证书匹配或实际冷启动成功。

## 生成 tag 约定

订阅协议字段不是 BoxForge 环境配置。转换遵循“显式来源字段 → 精确映射 →
仅缺失时的调优 → 核心默认值”；无法等价表达的字段导致整次 strict 转换失败，
non-strict 调用跳过整个节点。Shadowsocks UoT 使用对象，v1 和 v2 均显式写版本；插件接受
Mihomo 的结构化选项，而非原始 SIP003 字符串。支持范围和限制见
[来源语义与调优优先级](architecture.md#来源语义与调优优先级)。

用户可见的 selector 和 URLTest 使用简短英文名称；内部 tag 使用 lowercase
kebab-case，并按对象用途区分。默认地区组如下，地区覆盖可修改或追加地区名称：

| 类型 | 固定 tag |
| --- | --- |
| 主 selector | `🚀 PROXIES` |
| 地区 selector | `🇺🇸 US`、`🇯🇵 JP`、`🇭🇰 HK`、`🇸🇬 SG` |
| 地区 URLTest | `🇺🇸 US AUTO`、`🇯🇵 JP AUTO`、`🇭🇰 HK AUTO`、`🇸🇬 SG AUTO` |
| 服务 selector | `🤖 AI`、`🔎 Google`、`🎵 Spotify`、`🎮 GAMES`、`🪟 Microsoft` |
| 直连 outbound | `DIRECT` |

内部基础设施 tag 如下：

| 类型 | 固定 tag |
| --- | --- |
| TUN inbound | `tun-in` |
| L3 bridge outbound（仅 Linux） | `bridge-out` |
| Tailscale endpoint / DNS | `tailscale`、`dns-tailscale` |
| 直连 DNS | `dns-direct-alidns`、`dns-direct-tencent` |
| 代理 DNS | `dns-proxy-google`、`dns-proxy-cloudflare` |
| rule-set HTTP client | `http-ruleset-proxy` |
| 可选 Dashboard HTTP client | `http-dashboard-direct` |
| sing-box API service | `api` |

DNS evaluate/respond 的响应 tag 使用 `response-范围-优先级`：
`response-priority-primary`、`response-priority-secondary`、
`response-domestic-primary`、`response-domestic-secondary`、
`response-global-primary`、`response-global-secondary`。
Linux/Windows 的 A 查询另用独立响应 tag，在上述六个名称后追加 `-a`：
`response-priority-primary-a`、`response-priority-secondary-a`、
`response-domestic-primary-a`、`response-domestic-secondary-a`、
`response-global-primary-a`、`response-global-secondary-a`。
这十二个响应 tag 均为全平台保留名称。
规则集使用 BoxForge 语义 tag：`ads`、`ai`、`google`、`microsoft`、
`spotify`、`games`、`cn`、`cnip`。外部文件名由来源映射决定，
不作为内部命名约定。

订阅中的真实节点保留来源名称。若节点名与任一有效地区或服务 tag、BoxForge 固定 tag、DNS 响应 tag
或 rule-set tag 冲突，strict 转换会直接报错；non-strict 调用记录警告并跳过整个
节点，不会静默追加后缀。这样可以避免
订阅更新后 selector 引用或已保存选择发生漂移。

这些 selector tag 同时是 Clash API 使用的标识。更改 tag 会改变 Dashboard
保存选择时的身份；BoxForge 不提供旧名称别名或已保存选择的自动迁移。

## 其他代码固定值

| 内容 | 固定值 |
| --- | --- |
| Tailscale 状态目录 | `tailscale` |
| `accept_routes` | `true` |
| `on_demand` | `true` |
| sing-box API（启用时） | `127.0.0.1:9090` |
| Dashboard 目录（启用时） | `dashboard` |

没有使用值的可选字段（`control_url`、`hostname`、`exit_node` 和
`exit_node_allow_lan_access`）不写入生成的 JSON，也不提供环境变量入口。

## Tailscale 运行说明

三个目标平台默认都生成一个 Tailscale endpoint。它复用 sing-box 已有的系统
VPN/TUN，不创建第二个系统 VPN 接口，并通过 `on_demand: true` 允许 endpoint
在无需使用时断开。登录状态保存在 `StateDirectory`，不会写入 `config.json`。

Tailscale 控制平面域名继承 `route.default_domain_resolver`，通过
`dns-direct-alidns` 建立初始连接。该解析器
默认以 IP 字面量 `223.5.5.5` 直连 AliDNS DoH，TLS `server_name` 为
`dns.alidns.com`；这两个值可通过[resolver profile](#resolver-profile) 覆盖。
它不调用系统解析器，也不经主代理组，避免明文
DNS 和冷启动循环依赖。

`taildrop_directory` 不提供环境变量入口，始终按目标平台生成：

| 平台 | 生成值 | 运行时含义 |
| --- | --- | --- |
| Android | `Taildrop` | SFA 工作目录下的 `Taildrop`；不直接写入公共 Download 目录 |
| Windows | `$USERPROFILE\Downloads\Taildrop` | sing-box 在运行时展开当前进程账户的 `USERPROFILE` |
| Linux | `$HOME/Downloads/Taildrop` | sing-box 在运行时展开当前进程账户的 `HOME` |

Android 的相对路径以 sing-box 工作目录为基准。Windows 和
Linux 的环境变量属于运行 sing-box 的进程账户；由系统服务运行时，
它们不一定指向桌面登录用户。目标账户必须对展开后的目录具有写权限。

发送和管理文件需使用 sing-box 图形客户端、Dashboard 或 `sing-box api`，
并在 Tailscale 管理端启用文件共享。

[返回 README](../README.md)
