# 配置参考

BoxForge 使用 profile 描述功能、DNS、路由和连接调优。
CLI 与 HTTP Server 共用配置规则；未提供覆盖时保留内置生成策略。
旧环境开关不再兼容；不会静默忽略旧键。

Core 的 profile 错误使用 `ProfileValidationException`（仍可按 `FormatException` 捕获），
其 `Diagnostics` 提供不含输入值的错误码和配置位置，详见[诊断与校验归属](architecture.md#诊断与校验归属)。
HTTP 错误格式保持不变。

## 配置表

<!-- BEGIN GENERATED:profile-defaults -->
由[内置 profile 源码](https://github.com/eurekaevan/BoxForge/blob/main/src/BoxForge.Core/Configuration/default-profile.json)生成；平台列仅列出内置覆盖，未列出的值继承根默认值。
对象数组的完整内容见内置 profile；下表的数组是默认数据，覆盖操作见后文。

更新事实来源后运行 `python3 scripts/generate-contract-docs.py`；CI 使用 `--check` 拒绝过期片段。

| 配置路径 | 根默认值 | 平台覆盖 |
| --- | --- | --- |
| `features.tailscale.enabled` | `true` | — |
| `features.tailscale.taildrop_directory` | `"$HOME/Downloads/Taildrop"` | Android: `"Taildrop"`；Windows: `"$USERPROFILE\\Downloads\\Taildrop"` |
| `features.api.enabled` | `false` | — |
| `dns.address_family` | `"Ipv4Only"` | — |
| `dns.selection_mode` | `"ParallelFastest"` | Android: `"SequentialFallback"` |
| `dns.timeout` | `"3s"` | — |
| `dns.cache_capacity` | `4096` | — |
| `dns.optimistic_timeout` | `"6h"` | — |
| `dns.resolvers` | 4 项（见内置 profile） | — |
| `route.regions` | 4 项（见内置 profile） | — |
| `route.services` | 5 项（见内置 profile） | — |
| `route.rule_sets` | 6 项（见内置 profile） | — |
| `route.main_default_region` | `"us"` | — |
| `route.sniff_timeout` | `"300ms"` | — |
| `route.sniff_excluded_ports` | `[21,22,3306,5900]` | — |
| `route.tun_addresses` | `["172.19.0.1/30","fd32:f937:4cd0:1::1/126"]` | — |
| `route.strict_route` | `true` | Android: `null` |
| `route.auto_redirect` | `null` | Linux: `true` |
| `route.multi_queue` | `null` | Linux: `true` |
| `route.use_direct_forwarding` | `false` | Linux: `true` |
| `tuning.connect_timeout` | `"5s"` | — |
| `tuning.tcp_keep_alive` | `"1m"` | Android: `null` |
| `tuning.tcp_keep_alive_interval` | `"30s"` | Android: `null` |
| `tuning.url_test_interval` | `"1m"` | Android: `"3m"` |
| `tuning.url_test_url` | `"https://www.gstatic.com/generate_204"` | — |
| `tuning.url_test_tolerance` | `50` | — |
| `tuning.url_test_idle_timeout` | `"30m"` | — |
| `privacy` | `{}` | — |
<!-- END GENERATED:profile-defaults -->

API 启用后生成仅监听 `127.0.0.1:9090` 的 sing-box service 和 Dashboard，
不改变 BoxForge HTTP Server 的监听地址。Dashboard 使用独立直连 HTTP client；
远程规则集通过地区 AUTO（若无则首个节点）下载。控制面不开放远程监听或共享 secret。

## 统一 profile

CLI 使用 `generate --profile profile.json`，Server 使用 `--profile profile.json`；
也可通过 `BOXFORGE_Profile` 指定启动文件。显式参数优先，相对路径以进程工作目录为基准。
Server 启动后使用不可变宿主快照；修改文件或环境变量需要重启。
HTTP 请求只接受内联 JSON，不能指定服务器路径或 URL。

Core 注册只读取传入的 `IConfiguration`，不会自行读取进程环境变量。
CLI、Server 在宿主边界加载并校验 `BOXFORGE_` 前缀的环境配置快照，再合并到宿主配置；
显式 `--profile` 路径仍优先于环境路径。嵌入其他宿主时，普通 `runtime`、`policies`、
`regions`、`services`、`rule_sets`、`resolvers` 节不属于 BoxForge 配置，也不会作为遗留键拒绝；
专用旧开关 `Tailscale`、`TailscaleEnabled`、`TailscaleAndroidEnabled`、`SingboxApi` 仍拒绝。
这不放宽 profile 文件或请求的严格结构校验；`BOXFORGE_regions` 等未知环境路径仍拒绝。

文件与请求均使用 JSON 对象。根对象接受 `features`、`dns`、
`route`、`tuning`、`privacy`、`platforms`。前五个分区保存跨平台值；
`platforms.Android`、`platforms.Linux`、`platforms.Windows` 是同结构的稀疏覆盖层，
可包含五个分区，但不得嵌套 `platforms`。根与平台层均无 `version` 字段；
提供该字段会按普通未知键拒绝。

对每个目标平台，依次应用：内置根值 → 用户文件根值 → 统一环境根值 →
内置、用户文件、环境各自的该平台层 → 请求根值 → 请求自身的该平台层。
平台层按来源顺序应用；请求仅影响当前转换，不改变宿主或并发请求。
省略字段继承前值。未知键、重复 JSON 键、无效类型或悬空引用均拒绝。
只有既有可空字段接受 `null`：`route.strict_route`、`route.auto_redirect`、
`route.multi_queue`、`tuning.tcp_keep_alive`、`tuning.tcp_keep_alive_interval`。

```json
{
  "features": { "tailscale": { "enabled": false } },
  "route": { "sniff_timeout": "500ms", "sniff_excluded_ports": { "replace": [22, 3306] } },
  "platforms": {
    "Android": { "features": { "tailscale": { "enabled": true } } },
    "Linux": { "route": { "tun_addresses": { "replace": ["172.20.0.1/30", "fd01::1/126"] } } }
  }
}
```

地址族接受 `Ipv4Only`、`DualStack`、`Auto` 或 `DirectDualStack`，默认使用 `Ipv4Only`（全局 IPv4，直连与代理流量都拒绝 IPv6）。
需要直连 IPv6 时可覆盖为 `DirectDualStack`：保留 TUN 双栈捕获，固定代理类别（服务分流和最终出口）仅使用 IPv4，
直连流量（私网、Tailscale、`cn`、`cnip`）保留 IPv6，并同样按当前出口接口是否具有公网 IPv6 地址决定是否拒绝 IPv6。
限制按固定类别生效，服务组或主组切到 DIRECT 也不解除该类别的 IPv6 限制。DNS 保留 AAAA，不承诺被拒绝连接会自动回退 IPv4。
`Auto` 同样保留 TUN 双栈捕获并按出口接口判断，但不限制代理流量的 IPv6。
它们都不执行公网可达性探测。Android 依赖客户端提供默认物理接口信息，尚未完成真机验证；
缺少接口信息时保守拒绝客户端 IPv6。详细边界见[DNS 与路由策略](routing-and-dns.md#dns-分类与控制面解析)。
DNS 模式接受 `SequentialFallback` 或 `ParallelFastest`。超时和间隔必须是正 Go duration；DNS 缓存容量为正，URL test
容差为 1–65535ms，默认 50ms；不接受 `0`，因为锁定核心会将它解释为默认 50ms，无法实现零容差。
探测 URL 只接受不带用户信息和片段的 HTTP(S) URL。
TUN 地址须包含 IPv4 和 IPv6 CIDR；`multi_queue: true` 仅支持 Linux。
排除端口为不重复的 1～65535 整数。调优只补齐来源节点缺失的值；
例如 `tcp_keep_alive: null` 取消平台默认值，不会清除节点显式设置。

### 数组操作

覆盖已有数组路径时不能直接写数组，必须使用显式操作对象。
整体操作 `replace`、`append`、`remove` 均接受数组，固定依次执行替换、追加、删除，
与 JSON 键的书写顺序无关。`replace: []` 清空，`append: []` 和 `remove: []` 不改变值。
空操作对象、未知操作、错误类型或找不到的删除项会拒绝整个覆盖。

```json
{
  "route": {
    "sniff_excluded_ports": { "append": [8080], "remove": [21] }
  }
}
```

`remove` 按元素完整 JSON 值深比较，不按 `id` 或 `tag` 匹配；对象属性顺序不影响匹配，
数组顺序属于值的一部分。叠加及 `remove` 使用原始配置值：例如规则源的
`update_interval: "1d"` 与 `"24h"` 不作为相同元素匹配，单 tag 的 URL 模板也不会提前展开。
整体操作不按 ID 隐式合并，也不拆分批量来源。

操作 payload 中的新实体是完整数据对象，其内部数组是普通数据数组，
例如追加服务的 `rule_sets: ["games"]`，或追加来源的 `tags: ["netflix"]`。
这与对已有数组路径进行覆盖所需的操作包装不同。新增实体必须包含所有必填字段。

对象数组另支持以下显式按键操作。地区、服务和 resolver 使用 `id`；规则集来源使用
`tag` 选择其 `tags` 中包含该标签的整个来源对象，修改会影响该组全部标签，不自动拆组。

| 操作 | 内容 | 位置语义 |
| --- | --- | --- |
| `patch` | 含选择键的部分对象，其余字段递归覆盖 | 保留原位置和未提供的字段 |
| `set` | 含选择键的完整对象 | 在原位置替换；缺少必填字段拒绝 |
| `remove_by_key` | 仅含选择键的对象 | 删除匹配项 |
| `insert_before` | `before` 为选择器，`value` 为完整新对象 | 在匹配项前插入 |

例如将 `ai` 的默认地区改成 `jp`，同时保留服务和同阶段路由顺序：

```json
{
  "route": {
    "services": { "patch": [{ "id": "ai", "default_region": "jp" }] }
  }
}
```

`patch` 中的嵌套数组仍使用显式操作，例如 `rule_sets: {"append":["games"]}`。
`set` 和 `insert_before.value` 的内部数组则是完整数据，如 `rule_sets: ["games"]`。
`id`/`tag` 作为选择器不能重命名实体。按 `tag` 操作分组来源时，`set` 仍需提供完整
`tags` 数组，且 `patch`/`set` 后必须保留所选标签。例如：

```json
{
  "route": {
    "rule_sets": { "patch": [{ "tag": "games", "update_interval": "12h" }] },
    "services": {
      "insert_before": [{
        "before": { "id": "google" },
        "value": {
          "id": "custom", "name": "Custom", "default_region": "jp",
          "rule_sets": ["games"], "precedes_domestic_routes": true
        }
      }]
    }
  }
}
```

多种操作组合时固定按 `replace → append → remove → patch → set → remove_by_key → insert_before`
执行，每种操作内部按列表顺序执行。`patch`/`set` 不会创建不存在的项，新项使用 `append`
或 `insert_before`。选择键区分大小写；缺失、空白、类型错误、找不到目标、匹配不唯一、
未知字段、重复身份或无效引用均拒绝整个覆盖，不修改原 profile。标量数组不支持按键操作。

### 环境变量

除选择文件的 `BOXFORGE_Profile` 外，每个 `BOXFORGE_` 键都必须对应真实 profile
路径；用 `__` 分隔对象层级，键名不区分大小写。例如：

```bash
BOXFORGE_features__api__enabled=true \
BOXFORGE_dns__timeout=8s \
BOXFORGE_route__sniff_excluded_ports='{"replace":[22,3306]}' \
dotnet run --project src/BoxForge.Cli -- generate --platform Linux
```

值按 JSON 标量或原始字符串解析，再按目标字段类型校验；布尔值使用 `true`/`false`，
数值使用 JSON 数字，时长可使用 `8s`。数组覆盖使用整个操作 JSON 对象。
环境键也可指向 `platforms__Android__features__tailscale__enabled` 等平台层路径。
未知键及旧 `Tailscale__Enabled`、`TailscaleEnabled`、`Tailscale__AndroidEnabled`、
`TailscaleAndroidEnabled`、`SingboxApi__Enabled` 均拒绝，不能作为兼容别名。
同一平台的 Tailscale 开关统一为 `features.tailscale.enabled`；平台差异由平台层表达。

### 查看有效配置

`profile show --resolved --profile profile.json` 输出纯 JSON，
`platforms` 中每个平台是已计算完成的五分区快照，不嵌套 `platforms`；
根与平台项均无 `version` 字段。
该输出用于观察与追溯，不是可直接重载的差异覆盖文件；其中数组是已解析的数据，
覆盖文件中的数组路径则要求操作对象；不支持 `profile diff` 或 JSON Schema。

## 地区 profile

`route.regions` 的完整实体为 `id`、`display_name`、`pattern`。
默认 ID 为 `us`、`jp`、`hk`、`sg`；ID 区分大小写，为以小写字母开头的
1～32 位小写字母、数字或连字符。追加地区示例：

```json
{
  "route": {
    "regions": { "append": [
      { "id": "tw", "display_name": "🇹🇼 TW", "pattern": "台湾|台灣|taiwan|TW[0-9]+" }
    ] }
  }
}
```

`pattern` 为 .NET 正则，大小写和文化无关，单次匹配最多 250ms；超时使整次转换失败。
JSON 中反斜杠需转义。地区匹配至少两个节点才生成 selector 和 AUTO；单个节点仍可
作为主组或服务组的默认目标。显示名不得含控制字符或首尾空白，生成 tag 不得与地区、服务或
固定保留 tag 冲突。移除地区前需同时处理 `main_default_region` 及服务的引用。

## 服务 profile

`route.services` 的完整实体包含 `id`、`name`、普通数组 `rule_sets`、
`default_region`、`precedes_domestic_routes`。默认服务 ID 为 `ai`、`google`、
`spotify`、`games`、`microsoft`；ID 规则与地区相同。追加示例：

```json
{
  "route": {
    "services": { "append": [
      { "id": "custom-games", "name": "Custom Games", "rule_sets": ["games"],
        "default_region": "us", "precedes_domestic_routes": true }
    ] }
  }
}
```

服务规则集不得为空或重复，必须引用最终 `route.rule_sets` 的 tag；偏好地区须存在。
服务名是 selector tag，不得与其他有效标签冲突。`precedes_domestic_routes` 选择
既有优先或普通服务阶段，不开放任意阶段或绕过守卫。DNS 服务域名池随最终服务列表更新。

## rule-set 来源 profile

`route.rule_sets` 保留完整来源实体：普通数组 `tags`、`url`、`update_interval`。
它不是按单个 `tag` 合并的补丁。添加 Netflix 来源及服务：

```json
{
  "route": {
    "rule_sets": { "append": [
      { "tags": ["netflix"], "url": "https://rules.example.net/netflix.srs", "update_interval": "1d" }
    ] },
    "services": { "append": [
      { "id": "netflix", "name": "Netflix", "rule_sets": ["netflix"],
        "default_region": "us", "precedes_domestic_routes": false }
    ] }
  }
}
```

`rules.example.net` 为占位地址，需替换为实际二进制 SRS HTTPS 来源。
标签必须唯一且不与固定标签、有效地区或服务 tag 冲突；格式与地区 ID 相同。
URL 不得含用户信息、片段、未编码空白或控制字符，只允许路径或查询中的字面量
`{tag}` 占位符。多 tag 来源必须保留该占位符。更新间隔接受正 Go duration，
并额外支持 `d`（24 小时），如 `1d`；总时长不能超出 int64 纳秒范围。
规则源 URL 模板展开与更新时长归一化仅用于发射目标配置；profile 快照保留原始
URL 与时长文本，完整值覆盖和删除均使用这些原值，不比较发射后的规范化形式。
修改批量来源时应替换完整来源实体；不会自动拆组。内置广告/国内规则集以及服务
引用必须仍有效，不能通过删除来源留下悬空引用。

## resolver profile

`dns.resolvers` 使用完整实体 `id`、`server`、`server_name`。
四个固定角色及 tag 如下。整体替换时须提供完整实体；局部修改使用按 `id` 选择的
`patch`，例如 `{"id":"alidns","server":"223.6.6.6"}`。

| 固定 ID | 固定 tag | 固定角色 |
| --- | --- | --- |
| `alidns` | `dns-direct-alidns` | 国内主解析器；控制面引导，直连 |
| `tencent` | `dns-direct-tencent` | 国内备用解析器，直连 |
| `cloudflare` | `dns-proxy-cloudflare` | GLOBAL 主解析器及 DNS final，经主代理组 |
| `google` | `dns-proxy-google` | GLOBAL 备用解析器，经主代理组 |

每个服务另生成 `dns-service-{id}-cloudflare` 和 `dns-service-{id}-google`，
复用对应 resolver 的地址和 TLS 名称，连接出口为该服务选择组。

`server` 为规范单播 IPv4 字面量，允许私网/loopback，不接受域名、IPv6、URL、端口、
CIDR、非规范数字、`0.*` 或首段大于等于 224 的地址。
`server_name` 为 ASCII DNS 名称（国际域名使用 punycode），总长 1～253、每 label
1～63 字符，只允许字母、数字和内部连字符，不接受 IP、通配符、尾点或空白。
TLS 始终启用，不开放明文回退、任意 detour 或主备角色改变。
更换 AliDNS 地址后 bootstrap CIDR、控制面及 HTTP client 仍使用相同固定 tag；
配置和核心检查不证明远端可达、证书匹配或实际冷启动成功。

## 生成 tag 约定

订阅协议字段不是 BoxForge 环境配置。转换遵循“显式来源字段 → 精确映射 →
仅缺失时的调优 → 核心默认值”；任一无法等价表达的节点使整次转换失败，
不返回部分结果。Shadowsocks UoT 使用对象，v1 和 v2 均显式写版本；插件接受
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
| 服务 DNS | `dns-service-{id}-cloudflare`、`dns-service-{id}-google` |
| rule-set HTTP client | `http-ruleset-proxy` |
| 可选 Dashboard HTTP client | `http-dashboard-direct` |
| sing-box API service | `api` |

DNS evaluate/respond 的响应 tag 使用 `response-范围-优先级`：
`response-service-{id}-primary`、`response-service-{id}-secondary`、
`response-domestic-primary`、`response-domestic-secondary`、
`response-global-primary`、`response-global-secondary`。
并行模式的 A 查询另用独立响应 tag，在上述名称后追加 `-a`：
`response-service-{id}-primary-a`、`response-service-{id}-secondary-a`、
`response-domestic-primary-a`、`response-domestic-secondary-a`、
`response-global-primary-a`、`response-global-secondary-a`。
允许放行的 AAAA 查询同样使用独立 tag，后缀改为 `-aaaa`。
这些响应 tag 均按有效 profile 的服务 ID 生成为全平台保留名称。
内置规则集使用 BoxForge 语义 tag：`ads`、`ai`、`google`、`microsoft`、
`spotify`、`games`、`cn`、`cnip`，可通过来源 profile 追加新 tag。外部文件名由来源映射决定，
不作为内部命名约定。

订阅中的真实节点保留来源名称。若节点名与任一有效地区或服务 tag、BoxForge 固定 tag、DNS 响应 tag
或 rule-set tag 冲突，整次转换会直接失败，不返回部分结果，也不会静默追加后缀。这样可以避免
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

`features.tailscale.taildrop_directory` 可通过文件、平台层或统一环境键覆盖，默认按平台设置：

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
