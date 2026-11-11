# 生成配置约定

本文记录内置 profile 的默认生成行为及 JSON 输出约定。平台参数、运行时开关与
策略可通过[统一 profile](configuration.md#统一-profile)覆盖。

## JSON 输出约定

输出层只省略 `null` 和空集合，不根据核心默认值删除字段，也不根据其他字段
推导并省略等价值。模型中已有的标量值均保留，包括空字符串、`false` 和 `0`；
规则、候选及 ALPN 列表保留原有顺序和重复项。

来源映射和 BoxForge 调优仍决定模型中有哪些值。未映射、未调优的可选字段可以
保持 `null` 并省略，因此这不表示生成配置已显式设置核心的全部默认值。
核心版本变化时仍需运行单元测试、三平台生成检查与官方核心验证。

## 平台差异

平台默认值来自内嵌的 `Configuration/default-profile.json`，由配置文档
`BoxForgeProfile` 叠加后解析成不可变的单平台 `ResolvedProfile`。CLI 的 `--profile` 和
`BOXFORGE_Profile` 均可覆盖平台参数，具体优先级见配置参考。
用户显式提供的节点参数优先于平台调优默认值，Tailscale 启用状态仍由
`features.tailscale.enabled` 及平台层覆盖控制；地址族使用有效 profile 的平台值。
DNS 顺序回退的首选 `2s` 预算属于算法常量，不是 Android 的全局 DNS 超时。

每个平台都只生成一个 TUN inbound，不提供本地 SOCKS/HTTP mixed 入口。
TUN 固定启用 `auto_route`，DNS 模式显式输出 `dns_mode: hijack`。
DNS 主要由 TUN 自身机制处理；首条路由以 `inbound: tun-in`、`port: 53`、
`action: hijack-dns` 显式兜底 TCP/UDP 端口 53，不依赖 sniff 识别 DNS 协议。
`strict_route` 默认仅在 Linux/Windows 显式启用；指定版本 Android feature matrix
标记该能力未实现，因此省略。不生成 `set_system_proxy` 或
`platform.http_proxy`，因此不会由
sing-box 自动修改系统 HTTP 代理设置。

| 平台 | 默认差异 |
| --- | --- |
| Android | 不为代理出站写入 TCP keepalive |
| Linux | `auto_redirect: true`、`multi_queue: true`；代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s`；地区 AUTO 探测周期 `1m` |
| Windows | 直连使用 `DIRECT`；代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s`；地区 AUTO 探测周期 `1m` |

三个平台均不生成已弃用的 `stack`，由 sing-box 1.15 使用 sing-tun 自有 TCP/IP
栈；也不生成 `mtu`，由 sing-box 按目标平台和运行环境采用默认值。
Linux 显式启用 `multi_queue: true`，允许自有栈通过 TUN 多队列并行处理；
Windows/Android 省略该字段。实际吞吐收益尚未验证，可按
[对照实验](protocol-validation.md#linux-tun-多队列对照)方法测量。

所有平台直连规则统一使用 `DIRECT`，不为私网或国内目标生成额外的 L3 转发层，
也不生成 `bridge` outbound 或 `bypass` 规则。

TUN 默认 IPv6 地址为 `fd32:f937:4cd0:1::1/126`，可通过 `route.tun_addresses` 替换。
ULA 前缀由安全随机源一次生成并固定在内置 profile 中，三个平台共用；生成配置时不会重新随机选择。
该默认值降低与其他软件通用前缀碰撞的概率，不保证每个部署唯一；与已有网络冲突时应在部署 profile 中覆盖。
默认 `Ipv4Only` 为全局 IPv4：保留双栈 TUN 捕获，但 AAAA 查询无条件返回空答，
单道 `ip_version: 6` 拒绝无条件生效，位于 DNS 劫持之后、任何 DIRECT、Tailscale 或 sniff 之前。
直连和代理流量均不使用 IPv6，应用能否重试 IPv4 由应用自身决定。
覆盖为 `DualStack` 时不生成 AAAA 空答或 IPv6 拒绝规则，客户端 DNS、路由与代理控制面均保留双栈。
TUN 捕获地址本身不代表 IPv6 连通能力。
详见[地址族策略](routing-and-dns.md#dns-分类与控制面解析)。

源节点显式提供 `ip-version: ipv4` 时，仍保留该节点的 IPv4 解析约束：
仅 `DualStack` 下为该节点设置独立的 `domain_resolver`，绑定 `dns-direct-alidns`、
`strategy: ipv4_only` 并禁用 optimistic cache。省略该字段的节点继承平台策略；
`Ipv4Only` 下继承已有的全局控制面 IPv4 限制，不重复生成节点 resolver。其他 `ip-version` 值仍拒绝。

`route.auto_redirect` 仅控制 TUN 的内核重定向实现，不改变路由规则或直连行为。
`auto_redirect: true` 支持 Linux 和 root Android；Android 需要 root 服务或 root shell，普通 VPN 客户端不支持。
Windows 的 `auto_redirect: true` 在 profile 校验时拒绝。
Windows、Android 默认 `auto_redirect` 为 `null`。

三个平台默认生成 Tailscale endpoint，并启用 `on_demand: true`。启用时，
`taildrop_directory` 来自有效平台 profile：默认 Android 使用
SFA 工作目录下的 `Taildrop`，Windows 使用
`$USERPROFILE\Downloads\Taildrop`，Linux 使用 `$HOME/Downloads/Taildrop`。环境
变量由目标机器上的 sing-box 在运行时展开。
TUN 的 Tailscale `preferred_by` 路由位于私网直连之前，保证通告子网优先走
Tailscale；未命中的私网地址仍按原策略走 DIRECT。

## DNS 与持久化缓存

- 默认启用 `route.block` 中的五条 B 站 PCDN 域名规则，分别生成 DNS NXDOMAIN 和嗅探后的路由 reject，
  位于广告规则之后、服务／国内分流之前。支持精确域名、后缀与限定通配符；
  覆盖方式及无域名／ECH 等限制见[自定义域名阻断](configuration.md#自定义域名阻断)。
- 所有 DNS `respond` 通过唯一 `match_response` 标签继承 evaluate 范围，不重复输出 `rule_set`；evaluate 和终止 SERVFAIL 仍保留分类范围。
- DNS 每次上游查询默认超时为 `3s`。Android 正常只查询首选 resolver，首选查询使用
  `2s` 超时，失败后才查询备用；Linux/Windows 同时查询两家。A 和允许放行的
  AAAA 查询优先让含对应地址族地址的 `NOERROR` 响应竞速，双方均无地址时才依次接受
  primary、secondary 的空答或仅 CNAME 等 `NOERROR` 后备；非地址查询让最快的
  `NOERROR` 胜出。全局 `optimistic.enabled` 为 `false`，仅保留正常 TTL 缓存。
  NXDOMAIN 不参与竞速，双方没有 `NOERROR` 时依次接受 primary、secondary 的
  NXDOMAIN。两家都失败时明确返回 `SERVFAIL`；详见
  [DNS 分类与回退](routing-and-dns.md)。
- DNS 默认使用 `Ipv4Only`，设置 `dns.strategy = ipv4_only` 并让全部客户端 AAAA 查询在规则链首部返回空 `NOERROR`；
  覆盖为 `DualStack` 时不强制客户端 DNS 的解析地址族，各解析池均保留 AAAA，也不生成 AAAA 空答规则。
  缓存容量为 `4096`，启用 reverse mapping；全局 `optimistic.enabled` 为 `false`，
  所有解析只使用 TTL 内的缓存。
- 代理节点和 Tailscale endpoint 继承
  `route.default_domain_resolver`：直连固定 tag `dns-direct-alidns`（默认 AliDNS，可覆盖端点）、禁用 optimistic
  过期缓存。`Ipv4Only` 强制该 resolver 使用 `ipv4_only`，使节点连接、
  rule-set 下载等代理控制面不依赖 IPv6，其域名解析不进入 client DNS 分类规则；仅 `DualStack` 不强制。
  `Ipv4Only` 在生成时拒绝 IPv6 字面量代理节点；DIRECT 显式绑定同一 DNS tag，`DualStack` 使用 `prefer_ipv4`，`Ipv4Only` 保持 `ipv4_only`。
- rule-set HTTP client 显式使用代理控制面解析设置；Dashboard HTTP client 直连，按平台地址族解析（`DualStack` 下不强制 IPv4），两者均禁用 optimistic 缓存；
  Tailscale split DNS 也禁用 optimistic 过期缓存。
- `experimental.cache_file` 使用 `cache.db`，并通过 `store_dns` 持久化 DNS 缓存。
- `cache_id` 是配置名、平台小写标识与生成 DNS 模型的结构化 JSON 经 UTF-8 编码后的 SHA-256，输出 64 位小写十六进制。
  配置名保留原样：CLI 使用输入文件名去掉扩展名；
  平台标识为 `android`、`linux`、`windows`。
  同名、同平台且 DNS 配置相同时稳定复用缓存，节点增减、排序、改名或元数据变化均不改变缓存身份；
  不同平台隔离。配置改名会更换身份，不同目录中的同名配置仍共享身份。
  更换 DNS 地址、TLS 名称、出口绑定或解析策略会切换命名空间，不再复用旧 DNS 答案。
  从旧算法升级时会冷启动一次；命名空间还保存选择器状态，因此升级或 DNS 配置变化后可能恢复默认选择。
  旧缓存数据保留；已选择节点被删除或改名时，该项选择仍会失效。

## 出站与 rule-set

- Clash YAML 中的重复键会在解析阶段拒绝，不会以“后值覆盖前值”
  的方式静默改变节点字段。
- 每种已支持节点协议先检查自己声明的 source schema。未支持或无法分类的
  显式连接字段使整次转换失败，不返回部分结果。
  已知的 UI 元数据和确认无连接作用的值会被明确列为忽略项。
- 出站模型不携带 BoxForge 调优默认值。正式生成时仅对缺失字段填入
  `connect_timeout: 5s`、Linux/Windows TCP keepalive `1m`/`30s`，
  以及未指定跳端口间隔的 HY2 `hop_interval: 30s`、`hop_interval_max: 60s`。
  没有正显式带宽时才补 `bbr_profile: standard`；显式固定间隔不补随机上限。
  已有显式出站值不会被调优覆盖；Android 不注入
  桌面 keepalive。以上模型值均显式输出。
- AnyTLS 的 `idle-session-timeout`、下划线别名以及旧
  `idle-timeout` 输入统一生成官方 `idle_session_timeout`；纯数字输入按秒转换。
  `idle-session-check-interval` 使用同一正 Go duration 校验：最多 128 字符，
  不得超过 int64 纳秒上限或小于 1ns；Mihomo 原生值是整数秒，BoxForge 另外
  保留 duration 文本扩展。两端会话库均将不超过 5 秒的维护值恢复为 `30s`。
  `min-idle-session` 映射 `0..2147483647`，只保证保留数量映射，不声称两端
  会话池的过期时间刷新算法完全一致。`client-metadata` 保留空白、Unicode 与 `=`，
  拒绝 LF 和超过 65479 UTF-8 字节的值，避免 settings 注入及 uint16 frame 溢出。
  字段未提供时省略，不注入会话维护值或客户端身份；AnyTLS Reality 不支持。
- SS、Trojan、Hysteria2、AnyTLS 密码保留原始值，包括前后空白；只拒绝缺失或空字符串，
  不对认证材料做 `Trim()`。
- VLESS `packet-encoding`（兼容 `packet_encoding`）按锁定 Mihomo 的实际语义映射：
  只有精确小写 `packetaddr` / `packet` 使用 `packetaddr`；其他值（含空值、不同大小写、
  未知值）以及缺失字段均使用 XUDP，JSON 显式输出 `packet_encoding: xudp`。
  不支持的 `packet-addr` / `xudp` 独立来源字段仍拒绝。
- VLESS `flow` 按来源规则处理：UTF-8 少于 16 字节时不启用 flow；否则前 16 字节
  必须为 `xtls-rprx-vision`，后缀（例如 `-udp443`）不传递到目标。其他前缀在转换阶段
  明确拒绝；目标模型中的非标准 flow 由自动化官方核心检查覆盖。
  flow 和 packet encoding 的依据是锁定的
  [Mihomo VLESS constructor](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/vless.go)。
- Reality 转换要求有效的 32 字节 Base64URL 公钥和显式 short ID。short ID
  可以为空，否则必须是最多 8 字节的偶数位十六进制字符串；错误会在节点转换阶段
  直接报告。还必须显式提供支持的 uTLS 指纹；VLESS 必须
  显式启用 `tls: true`，不会因存在 Reality 配置而隐式改变来源 TLS 设置。
- Hysteria2 显式 `up`/`down` 转为精确整数 Mbps；无法精确转换时拒绝。
  来源整数单位区分 bits/bytes；小数、`100 Kbps` 和溢出不会被截断为 Mbps。
  显式 `hop-interval` 为整数秒或范围，范围按来源排序、最小值不低于 5 秒，
  零值使用 `30s`；因此 `0-10` 和 `10-0` 均按锁定来源规则得到 `30s/30s`，
  `20s` 不是来源合法输入。固定间隔不注入随机上限；缺失时
  保留 BoxForge 的 `30s/60s` 调优，没有正显式带宽才补 `standard`。
  Gecko 保留独立密码及包大小，0 等价于缺失，默认 `512/1200`，有效范围为
  `1 <= min <= max <= 2048`，包大小仅可用于 Gecko。UDP MTU、握手超时、
  QUIC receive-window、Realm 拓扑和 HY2 uTLS 没有精确映射，仍明确拒绝。
- Shadowsocks UoT 使用对象形式：未指定版本或来源版本 0 使用 Mihomo 默认 v1，
  v1 和 v2 均显式输出 `version`。未启用时省略 UoT。
  插件只接受 Mihomo 结构化选项，不接受原始 SIP003 字符串；输出值按 SIP003 转义。
  `obfs` 的 HTTP/TLS 映射为 `obfs-local`，显式保留默认 host `bing.com`。
  仅支持上述 `obfs` 插件，`v2ray-plugin` 明确拒绝。来源没有 `network` 字段，
  因此不能将该字段转换为目标网络限制。
- 未支持的 TCP/TLS 选项（见下节）、AnyTLS disable-reuse
  以及不能精确传递的 Shadowsocks 插件配置
  当前均 fail-fast；详见[来源语义与调优优先级](architecture.md#来源语义与调优优先级)。
- 远程 rule-set 默认每天更新，通过默认 HTTP client `http-ruleset-proxy` 走代理下载。
  复用主组按 `route.main_default_region`（默认 `us`）规划出的初始默认目标：
  地区有多个节点时使用其默认 AUTO，只有一个节点时直接使用该节点；该地区无节点时
  复用主组的回退顺序。下载出口固定绑定该 AUTO 或节点，即使主组被手动切到
  `DIRECT`，rule-set 下载也不会随之改走直连。下载域名仍由
  `dns-direct-alidns` 直连解析，以免代理 DNS 成为冷启动依赖；
  `Ipv4Only` 设置 `ipv4_only`，`DualStack` 不强制解析地址族。
- 规则集来源与内部 tag 的映射如下；代码中的 DNS/route 引用只使用内部 tag，
  不依赖上游文件名。八个 tag 均唯一声明，默认按 `1d` 更新；可通过
  [profile 显式数组操作](configuration.md#数组操作)替换完整来源实体，不自动拆分批量来源。
  URL 模板展开与更新时长归一化仅用于目标发射；profile 快照保留原始配置值，
  完整值叠加和 `remove` 按原始 URL、时长文本匹配，不按生成后的值匹配。

  | 来源 | 内部 tag → 文件名 | URL 基础路径 |
  | --- | --- | --- |
  | DustinWin release | `games→games.srs`、`cn→cn.srs`、`cnip→cnip.srs` | `https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/` |
  | MetaCubeX `sing` 分支 | `ai→category-ai-!cn.srs`、`spotify→spotify.srs`、`ads→category-ads-and-telemetry.srs`、`google→google.srs`、`github→github.srs` | `https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/sing/geo/geosite/` |

  DustinWin 同名文件共用 `{tag}.srs` 模板；MetaCubeX 的文件逐项声明。
  AI 使用 MetaCubeX 的国外 AI 分类，Spotify 使用其明确域名集合；不额外叠加
  DustinWin 的关键词及独有条目。服务组名称、默认地区和 DNS 出口绑定保持不变。
  广告与遥测过滤的内部 tag 仍为 `ads`，使用 MetaCubeX 的
  `category-ads-and-telemetry`，替代 DustinWin 的 anti-AD 来源。该集合合并
  `category-ads` 与 `category-telemetry`，拦截广告、追踪及上游收录的软件遥测、
  错误报告和诊断域名；不代表覆盖所有软件遥测。不使用还包含服务商自身域名的
  `category-ads-all`。域名过滤仍可能误伤共享服务，不能保证零误拦。
  Games 使用 DustinWin 的 `games`，不限于单一游戏平台，并明确排除
  `games-cn`；具体覆盖范围由上游规则维护。
- `Ipv4Only` 下，全部 IPv6 使用原生 `ip_version: 6` 匹配，并在 DIRECT、Tailscale 和所有
  代理业务路由之前被拒绝。不生成仅供 mixed 域名目标使用的 `resolve` 和复检规则。
- TCP HTTP/TLS 嗅探排除 `21/22/3306/5900`，避免这些端口上客户端等待服务器
  先发数据时产生嗅探超时等待；排除只作用于 sniff，不跳过任何后续分流或拒绝规则。
  其他端口继续使用 `300ms` 嗅探预算，保留 `8080/8443` 等非标准 Web 端口的
  域名识别能力。自定义端口上的静默客户端仍可能等待超时；将 Web 服务部署到
  上述排除端口时，只能依靠 DNS 映射等已有信息进行域名分流。
- 未被前置 Tailscale 或私网直连规则处理的 UDP 流量会同时嗅探
  QUIC 和 STUN，并拒绝识别出的 STUN 协议；不根据 3478、3479、19302 或
  19303 等固定端口拒绝普通 UDP 流量。
- TCP、UDP 的所有端口均按优先服务 → 国内直连 → 普通服务 → 最终出口分流。
  UDP/443 不因端口统一拒绝，也不强制 QUIC 回退 TCP；STUN、广告以及 `Ipv4Only` 的 IPv6 拒绝仍生效。
  实际 QUIC/HTTP/3 可用性取决于所选代理和网络的 UDP 支持。

## TCP、TLS 与 Reality

VLESS 和 Trojan 只支持原生 TCP，`network` 可以省略或设为 `tcp`。
非 `tcp` 的 network 以及 `ws-opts`、`grpc-opts`、`http-opts`、`h2-opts`、
`xhttp-opts` 等旧传输选项明确拒绝；下划线别名同样拒绝，不能静默降级为 TCP。
生成的节点不包含 V2Ray `transport` 对象。VLESS 可使用明文 TCP 或显式启用
TLS，Trojan 始终启用 TLS；两者保留受支持的 uTLS 与 Reality 映射。

`alpn` 映射为 `tls.alpn`，保留有效顺序、重复项和值；缺失或空列表时省略，
不主动注入 ALPN。`sni`/`servername` 映射为 TLS server name，缺失时沿用
来源服务器名称。

`client-fingerprint` / `client_fingerprint` 只接受等价支持的 `chrome`、`firefox`、
`safari`、`ios`、`android`、`edge`、`360`、`qq`、`randomized`。空值或 `none`
使用普通 TLS，不输出 `utls`；不主动填入指纹。经典版本（`chrome120`、
`firefox120`、`safari16`）、`chrome_psk*` / `chrome_pq*` 历史别名及未知值明确拒绝，
避免目标无法启动或静默变成现代 Chrome。`random` 也拒绝，因为两端选择范围和分布不同。
对照来源为锁定的 [Mihomo 指纹实现](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/component/tls/utls.go)
和 [sing-box 指纹实现](https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/common/tls/utls_client.go)。

TLS 仍拒绝 `fingerprint`、`name-cert-verify`、`certificate`/`private-key`，以及
ShadowTLS、Restls、JLS、tlsmirror、ECH；非空 VLESS encryption 和 Trojan
ss-opts 也不支持。name-cert-verify 不能映射为改变 SNI 的 server_name；mTLS
的路径/inline PEM、链与密钥处理未完成可靠转换。

证书指纹的两端 hash 都可使用整张 DER 的 SHA-256，叶证书 pin 都替代普通
CA/名称验证，而非叠加校验。但 Mihomo 还能匹配中间/根证书，并在该分支验证
证书链和名称；目标 `certificate_sha256` 只匹配叶证书。仅凭源 hash 无法判断
层级，所以不生成该字段，也不进行未经证明的 hex→base64 转换。

### 源码依据与验证边界

各协议及共享 TLS 的来源/目标解释与固定 revision 链接保存在
[协议映射数据表](data/protocol-mappings.json)，由脚本生成[证据参考](protocol-mappings.md)。
表中 ID 仅用于文档和测试；内部 IR 保存解释后的值，仅在校验确需区分缺失与显式 null 时保留显式标记，
不携带来源字段路径或规则 ID。
五个协议统一通过 Schema 校验与协议解释钩子构建中立节点；目标发射不读取证据表。

研究基线：Mihomo Meta `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`，
sing-box `1.15.0-alpha.8` / `b609f959f57ce34416c51c7b87ce4a76f2e1df56`。
<!-- BEGIN GENERATED:core-baseline -->
当前最低支持与资格验证核心为官方 sing-box `1.15.0-alpha.12`，
revision `25b0d5ea3e466d246c708ac0f20d0448110e566c`。版本、revision 与归档摘要统一来自
[核心锁定数据](data/sing-box-core.json)；不代表验证了所有后续版本或定制构建。
<!-- END GENERATED:core-baseline -->
上述固定源码研究与当前核心检查的证据用途不同，不能以核心检查通过替代源码语义对照。
官方 Mihomo [TLS](https://wiki.metacubex.one/en/config/proxies/tls/) 文档与固定源码交叉核对：

- Mihomo [VLESS](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/vless.go)、
  [Trojan](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/trojan.go)、
  [证书链 pin](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/component/ca/fingerprint.go)。
- sing-box [TLS 客户端与 leaf pin](https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/common/tls/std_client.go)。

源码对照与本地 sing-box 回环提供不同层次的证据，均不能代替真实服务端和设备验证。
完整命令与 fixture 覆盖见[协议验证边界](protocol-validation.md)。

## sing-box API

<!-- BEGIN GENERATED:api-setting -->
API 默认 `features.api.enabled = false`。将该键设为 `true`
（环境变量 `BOXFORGE_features__api__enabled=true`）后，顶层增加一个仅监听
`127.0.0.1:9090` 的 `api` service，并启用工作目录下的 `dashboard`。
<!-- END GENERATED:api-setting -->
Dashboard 下载使用独立的 `http-dashboard-direct` 直连 HTTP client；允许的浏览器
origin 被限制为同端口的 `127.0.0.1` 与 `localhost`，
`access_control_allow_private_network` 保持
`false`。禁用时顶层 `services` 字段完全省略。

## 节点与分组

- 同一地区至少命中两个节点时，同时生成地区 selector 和对应的地区 AUTO：
  `🇺🇸 US AUTO`、`🇯🇵 JP AUTO`、`🇭🇰 HK AUTO`、`🇸🇬 SG AUTO`。
  地区 AUTO 只测试该地区真实节点；地区 selector 保留逐节点人工选择，并默认
  选择自己的 AUTO。
- 地区 URLTest 显式设置探测 URL `https://www.gstatic.com/generate_204`、
  `tolerance: 50`、`idle_timeout: 30m` 和 `interrupt_exist_connections: false`，
  因此 AUTO 切换不主动中断已有连接。Linux/Windows 使用 `interval: 1m`，
  Android 使用 `interval: 3m`。缩短周期用于更及时地发现节点延迟变化，
  不保证单连接提速。现有 selector 继续生成 `interrupt_exist_connections: true`。
- 主 `🚀 PROXIES` selector 依次保留地区组、单个节点和 `DIRECT` 的人工
  选择能力，不生成跨地区的全局 URLTest。默认选择 `route.main_default_region`
  对应的地区组（内置为 `🇺🇸 US`）；该地区仅有一个节点时直接选择该节点。
  该地区没有节点时，依次回退到首个已生成地区组、首个真实节点和 `DIRECT`。
- AI、Google、GitHub 和 Spotify 服务组在美国地区组存在时默认选择它；
  Games 在香港地区组存在时默认选择它。偏好地区仅有一个 leaf 时直接选择该节点，
  零个 leaf 才回退主代理组；偏好定义仅来自 `route.services`。
  Service selector 不直接引用地区 AUTO，而由地区 selector 默认到 AUTO。
  单个 US 与多个 JP 并存时，主组和偏好 US 的服务均默认选择 US 节点。
- `precedes_domestic_routes` 默认 `true`，所有内置服务均先于国内路由；显式设为 `false` 才移到国内路由之后。
  服务同时生成查询前 DNS 分类，禁止引用内置 IP 专用 `cnip`，包括与其他 tag 混用；自定义集内容仍需由核心加载检查。
  `ads` 仍可引用，但广告 DNS NXDOMAIN 和路由拒绝先于任何服务，不能通过服务声明绕过。
  DNS 同步按优先服务池 → DOMESTIC（`cn` 域名）→ 普通服务池 → GLOBAL 排列，服务阶段各自保留列表顺序。
  DNS 查询前无法确定仅按 `cnip` 匹配的目标 IP 归属，因此不保证 DNS 查询出口总与业务出口相同。
- 真实代理节点保留订阅名称，但不得与 BoxForge 固定分组、内部基础设施、DNS
  响应或 rule-set tag 冲突；冲突会在平台规划阶段以 `IR009` 报错，不自动改名。

[返回 README](../README.md)
