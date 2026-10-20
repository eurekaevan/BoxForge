# 配置参考

BoxForge 使用统一 profile 保存运行时开关、全局策略、平台参数、地区、服务、rule-set 来源和 resolver。
不提供覆盖时，生成内容与内置默认配置一致。
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

## 统一 profile

CLI 使用 `generate --profile profile.json`，Server 使用 `--profile profile.json` 在启动时
加载 UTF-8 JSON 文件；也可以使用 `BOXFORGE_Profile` 或宿主配置键 `Profile`。
显式 `--profile` 优先选择文件，相对路径以进程工作目录为基准。Server 修改文件或
环境变量后需重启。HTTP 请求只接受内联 JSON，不能指定服务器文件路径。

配置按以下顺序应用，后者覆盖前者：内置默认值 → 文件 → 宿主环境配置 → 请求内联
`profile`。宿主环境中的现有 Tailscale/API 开关继续有效；请求覆盖只影响当前请求，
不会修改宿主快照或其他请求。

文件和请求内联对象都必须提供 `version: 1`。顶层接受 `version`、`runtime`、
`policies`、`platforms`、`regions`、`services`、`rule_sets`、`resolvers`。省略字段继承已有值；普通数组整体替换，不按索引或元素追加。
地区和服务继续按 `id` 合并，rule-set 来源按 `tag` 合并，resolver 按固定 `id` 合并，
具体规则见后文。未知键、重复 JSON 键、重复标识和无效值会被拒绝。
只有明确可空的平台字段接受 `null`；其他字段不能使用 `null` 表示继承或删除。

例如同时关闭桌面 Tailscale、调整 sniff 并替换 Linux TUN 地址：

```json
{
  "version": 1,
  "runtime": { "tailscale": { "enabled": false } },
  "policies": { "sniff_timeout": "500ms", "sniff_excluded_ports": [22, 3306] },
  "platforms": {
    "Linux": { "connect_timeout": "8s", "tun_addresses": ["172.19.0.1/30", "fd00::1/126"] }
  }
}
```

### runtime 与 policies

`runtime.tailscale.enabled`、`runtime.tailscale.android_enabled` 和
`runtime.singbox_api.enabled` 分别对应上表三个开关，接受 JSON 布尔值。

| `policies` 字段 | 默认值 | 用途 |
| --- | --- | --- |
| `main_default_region` | `us` | 主代理组偏好的地区 ID |
| `sniff_timeout` | `300ms` | 路由 sniff 超时 |
| `sniff_excluded_ports` | `[21, 22, 3306, 5900]` | TCP sniff 排除端口列表 |
| `url_test_url` | `https://www.gstatic.com/generate_204` | AUTO 组探测 URL |
| `url_test_tolerance` | `50` | AUTO 组延迟容差 |
| `url_test_idle_timeout` | `30m` | AUTO 组空闲超时 |
| `dns_timeout` | `5s` | DNS 查询超时 |
| `dns_cache_capacity` | `4096` | DNS 缓存容量 |
| `dns_optimistic_timeout` | `6h` | DNS 乐观缓存超时 |

### platforms

`platforms` 只接受 `Android`、`Linux`、`Windows`，每个平台只写需要覆盖的字段。
`is_mobile`、`address_family`、`use_direct_forwarding`、`url_test_interval`、
`dns_selection_mode`、`taildrop_directory`、`connect_timeout` 和 `tun_addresses`
均不可为 `null`。`strict_route`、`auto_redirect`、`multi_queue`、`tcp_keep_alive`、
`tcp_keep_alive_interval` 可设为 `null`，用于省略对应目标字段。
地址族使用 `Ipv4Only` 或 `DualStack`；DNS 模式使用 `SequentialFallback` 或
`ParallelFastest`。`tun_addresses` 是完整替换的 CIDR 地址数组，内置值为
`["172.19.0.1/30", "fd00::1/126"]`；`connect_timeout` 内置值为 `5s`。
`multi_queue: true` 仅支持 Linux。TUN 地址须同时包含一个 IPv4 和一个 IPv6 CIDR，
IPv6 地址用于捕获并执行相应地址族策略。超时和间隔必须为正 Go duration；排除端口
须在 1～65535 范围内且不重复，空数组表示不排除端口。探测 URL 只接受不含用户信息
或片段的 HTTP(S) URL，tolerance 非负，DNS 缓存容量为正。其他平台差异可通过 resolved 输出查看。
连接调优仍只补齐源节点未指定的字段；例如 `tcp_keep_alive: null` 取消平台默认值，
不会清除源节点显式提供的 keepalive。

`profile show --resolved --profile profile.json` 将最终不可变快照直接序列化为纯 JSON
输出到 stdout，包含运行时、策略及全部平台，可重定向留档。不指定文件时也能查看
默认值和环境配置合并后的结果。完整快照用于检查与追溯，不能直接作为差异覆盖文件
重新加载（例如来源快照使用 `tags`，覆盖使用 `tag`）。本阶段没有 `profile diff` 或 JSON Schema。

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
地区内只接受 `id`、`display_name`、`pattern`；其他 profile 分区见各节。

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
`rule_sets` 整体替换，不能是空数组，元素必须唯一并引用内置或同一文件 `rule_sets`
新增的 tag；服务条目本身不定义下载来源。`default_region` 必须引用合并后的地区 ID，可在同一文件
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

`rule_sets` 按已有 tag 覆盖 URL 或更新间隔，也可以追加新的远端规则集；不能删除或
改名已有 rule-set：

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
每个条目只接受 `tag`、`url`、`update_interval`。`tag` 必填且不能重复。
对内置 `ai`、`spotify`、`games`、`cn`、`cnip`、`ads`、`google`、`microsoft`，
省略字段继承该 tag 的默认来源；`rule_sets: []`、仅指定内置 tag 或填写相同默认值
不会拆分原分组，也不改变生成结果。

新 tag 必须为 1–32 个小写字母、数字或连字符，并以小写字母开头；必须提供
`url`，省略 `update_interval` 时默认 `1d`。新 tag 不能与固定保留 tag、有效地区
或服务 tag 冲突，订阅节点同样不能占用它。未知键、重复键、重复 tag、悬空服务引用
和显式 `null` 均拒绝。

例如，添加 Netflix 分流及其下载来源：

```json
{
  "version": 1,
  "rule_sets": [
    { "tag": "netflix", "url": "https://rules.example.net/netflix.srs" }
  ],
  "services": [
    { "id": "netflix", "name": "Netflix", "rule_sets": ["netflix"] }
  ]
}
```

这里的 `rules.example.net` 是占位地址，必须替换为实际托管 Netflix 二进制 SRS
的 HTTPS 来源。该服务默认使用主代理组，位于普通服务阶段；若需偏好地区，可设置
`default_region`。

URL 只接受绝对 HTTPS 地址，不能含用户信息、片段或未编码的空白/控制字符。
只识别字面量 `{tag}`，且它只能位于路径或查询部分；不允许其他或不完整占位符。
拆出的单 tag 会展开 URL 中所有 `{tag}`，例如示例的 `ai` 生成
`https://rules.example.net/ai.srs`。只改间隔时也继承并展开原 URL。

内置 `ai/spotify/games/cn/cnip` 共用一个 `{tag}` 批量来源。覆盖其中一个成员时，
仅将它拆出为独立来源，未覆盖成员保留原 URL 和间隔；原组只剩一个成员时展开其
URL，全部覆盖时不留下空组。处理顺序固定为原来源顺序，各组中保留成员在前、
拆出成员按原 tag 顺序排列，不受覆盖文件条目顺序影响。新增定义追加在全部原有
来源之后，按覆盖文件中的条目顺序排列。服务和 DNS/路由仍引用 tag，不随 URL
更换而变化。

`update_interval` 必须是带单位的正 duration，最长 128 字符，不超过
`9223372036854775807ns`。支持 `ns/us/µs/μs/ms/s/m/h/d`、小数、组合和前导 `+`，
其中 `d` 为 24 小时；例如 `.5d`、`1d12h`。不接受零、负值、裸数字或 `w/M`。
包含小数的值按精确数值截断至纳秒，并规范化为整数 `ns` 输出，例如 `.5d` 为
`43200000000000ns`，避免核心浮点舍入导致边界值溢出；普通整数单位表示保持原样。
语法依据[固定核心依赖的 duration 解析器](https://github.com/SagerNet/sing/blob/4ca3bebe0b8e/common/json/badoption/internal/my_time/format.go)；AnyTLS 的 Go duration 字段仍不接受 `d`。

生成格式固定为 remote/binary，下载仍使用 `http-ruleset-proxy`，不开放 `format`、
`type`、本地路径、HTTP client 或规则内容注入。路由阶段顺序和 IPv6 守卫保持不变。
`profile show --resolved` 展示合并、拆组后的实际来源（`tags` 数组），用于检查与留档。
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
内置规则集使用 BoxForge 语义 tag：`ads`、`ai`、`google`、`microsoft`、
`spotify`、`games`、`cn`、`cnip`，可通过来源 profile 追加新 tag。外部文件名由来源映射决定，
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
