# DNS 与路由优先级

sing-box 规则顺序会直接改变行为，因此 BoxForge 将生成顺序视为可测试的公开约定。
本文记录内置 profile 的默认输出顺序，便于修改规则时评估优先级影响。
平台参数、服务及策略可通过[统一 profile](configuration.md#统一-profile)覆盖；阶段顺序保持不变。

## 路由规则顺序

以下按 `SingboxRouteEmitter` 固定展开的阶段列出实际执行顺序。修改路由规则时，
需同步维护本节说明。IPv6 守卫在 `Ipv4Only` 下无条件生效，在 `Auto` 与 `DirectDualStack` 下按出口接口状态生效，
`DirectDualStack` 另在代理服务和最终出口之前拒绝 IPv6；Tailscale 阶段仅在
对应平台启用 Tailscale 时生成规则；最终出口由 `route.final` 指定。

仅 Linux 使用显式阶段扩展 `DIRECT` 规则。私网地址和主 DoH bootstrap 地址（默认 `223.5.5.5`）在
首个 `sniff` 前加入 `bridge-out` L3 forwarding；Linux 还先尝试
`auto_redirect` kernel-level `bypass`。需要嗅探或后续优先级才能判定的国内
直连规则保持原位，只在 UDP sniff 后加入 `tun-in` + UDP 限定的 bridge/bypass
伴随规则。每条原 `DIRECT` 都保留为回退。

1. **`DnsHijack`**：以 `inbound: tun-in` 和 `port: 53` 兜底劫持 TCP/UDP DNS；主要入口是 TUN 自身的 `dns_mode: hijack`，不依赖后续 sniff 的协议识别。
2. **`EarlyIpv6Guard`**（在 `DnsHijack` 之后）：`Ipv4Only` 时，以全局 `ip_version: 6` 拒绝全部 IPv6；`Auto` 与 `DirectDualStack` 时仅在缺少公网 IPv6 的出口上拒绝，先于 sniff、DIRECT、bridge 和 bypass。
3. **`Tailscale`**（在 `EarlyIpv6Guard` 之后）：启用 Tailscale 时，路由 endpoint 声明为首选的目标，优先于私网直连。
4. **`PreSniffDirect`**（在 `Tailscale` 之后）：直连私网地址和有效 profile 的 `alidns.server`（默认 223.5.5.5）；Linux 在每条 DIRECT 前依次展开 bypass、bridge。
5. **`Sniff`**（在 `PreSniffDirect` 之后）：依次嗅探 TCP HTTP/TLS 与 UDP QUIC/STUN；TCP 跳过 FTP、SSH、MySQL、VNC 常用端口 21/22/3306/5900，继续后续路由，8080/8443 仍嗅探。
6. **`BlockStunAndAds`**（在 `Sniff` 之后）：依次拒绝识别出的 UDP STUN 和 ads。
7. **`LateIpv6Guard`**（在 `BlockStunAndAds` 之后）：`Ipv4Only` 时，保留独立的全局 `ip_version: 6` 后置拒绝；`Auto` 与 `DirectDualStack` 时使用相同出口条件，先于所有代理服务路由。
8. **`ServiceIpv6Guard`**（在 `LateIpv6Guard` 之后）：仅 `DirectDualStack` 生成。仅对优先服务的规则集匹配 `ip_version: 6` 并拒绝。
9. **`PriorityServices`**（在 `ServiceIpv6Guard` 之后）：按服务定义顺序路由 🤖 AI、🔎 Google、🐙 GitHub，优先于国内 UDP/443 例外。
10. **`DomesticUdp443`**（在 `PriorityServices` 之后）：依次放行 cn、cnip 的 UDP/443；Linux 的 DIRECT 伴随规则仅匹配 TUN UDP。其他 UDP/443 继续后续服务分流和最终代理。
11. **`OtherServiceIpv6Guard`**（在 `DomesticUdp443` 之后）：仅 `DirectDualStack` 生成，对其他服务的规则集拒绝 IPv6，保留国内 UDP/443 先行直连的优先级。
12. **`OtherServices`**（在 `OtherServiceIpv6Guard` 之后）：按服务定义顺序生成其他服务分流：🎵 Spotify、🎮 GAMES。
13. **`DomesticDirect`**（在 `OtherServices` 之后）：依次按 cn、cnip 直连国内目标；Linux 的 DIRECT 伴随规则仅匹配 TUN UDP，TCP 仍由原 DIRECT 处理。
14. **`FinalIpv6Guard`**（在 `DomesticDirect` 之后）：仅 `DirectDualStack` 生成，以全局 `ip_version: 6` 拒绝所有未被前面规则直连的 IPv6，保证没有 IPv6 进入最终代理出口。
15. **`FinalOutbound`**（在 `FinalIpv6Guard` 之后）：未命中规则的流量使用 route.final 的 🚀 PROXIES；不额外生成 catch-all 规则。

`DomesticUdp443`、`DomesticDirect` 阶段中的国内 `DIRECT` 在 Linux 会紧邻原规则之前生成 L3
伴随规则：bypass 后接 bridge。伴随规则仅匹配
`tun-in` UDP，原规则的 rule-set、端口与服务顺序条件不变；TCP 在 sniff
后不会尝试这些 L3 层，仍由原 `DIRECT` 处理。Windows 和 Android 不生成伴随规则，使用原有 `DIRECT`。

全平台仅生成 TUN inbound，不生成 mixed 入口及其专用的域名解析和解析后复检规则。

嗅探覆盖提前放行路径之外的大多数 TUN TCP 和 UDP，发生在业务分流之前。
`route.sniff_timeout` 默认为 `300ms`，表示嗅探的超时预算，并非每条连接固定增加
300ms；能快速识别或结束嗅探的连接可提前继续，等待数据或识别的连接则可能增加首包等待。
该预算也不代表连接建立的总耗时上限。参见官方 [sniff 说明](https://sing-box.sagernet.org/configuration/route/rule_action/#sniff)。

可通过统一 profile 调整 `route.sniff_timeout`；`route.sniff_excluded_ports` 仅排除 TCP
目标端口，不影响 UDP QUIC/STUN 嗅探。缩短预算或增加排除端口可能降低协议和域名识别率，
进而影响依赖嗅探结果的分流；仅保留常见 Web 端口也会遗漏非标准端口上的 HTTP/TLS。
评估调整时应分别测量快速识别、无法识别和等待服务端先发数据的连接，不能从配置值推导固定延迟。

对业务分流而言，核心优先级是：

```text
IPv6 早期拒绝 → 广告拒绝 → IPv6 兜底拒绝 → 代理服务 → 国内 IPv4 → 最终代理
```

`Ipv4Only` 下，所有代理服务路由位于后置 IPv6 拒绝之后，确保服务分流只处理 IPv4 目标。
`DirectDualStack` 下，各服务类别在自己的阶段拒绝 IPv6；出口条件允许且未先命中代理服务类别时，国内 `cn`/`cnip` 的 IPv6 直连，
其余 IPv6 在最终出口之前被拒绝，核心优先级为：

```text
接口 IPv6 早期拒绝 → 广告拒绝 → 接口 IPv6 兜底拒绝 → 优先服务 IPv6 拒绝及分流 → 国内 UDP/443 直连 → 其他服务 IPv6 拒绝及分流 → 国内直连 → 其余 IPv6 拒绝 → 最终代理
```

地址族限制采用固定路由类别，不跟随 selector 最终叶子的运行时切换。将服务组或主组切到 DIRECT，不会解除该类别的 IPv6 限制。要放开该类别需更改 profile 策略；官方核心不提供按最终叶子匹配的路由条件。

## DNS 分类与控制面解析

每个目标最终配置中的 `dns.address_family` 决定地址族；
内置 JSON 为 Linux、Windows、Android 均设置 `Ipv4Only`（全局 IPv4）。该策略生成
`dns.strategy = ipv4_only`、最前面的全局 AAAA 空 `NOERROR`，以及 route 的早期和
后置 IPv6 reject，后者也覆盖字面量 IPv6 和不经 sing-box DNS 的连接；直连与代理流量都不使用 IPv6。
通过 `platforms.<平台>.dns.address_family` 可覆盖为 `DirectDualStack`（代理仅 IPv4、直连保留 IPv6）、
`Auto` 或 `DualStack`。`DualStack` 不生成上述
AAAA 阻断和 IPv6 reject，DNS 与控制面解析策略也随之切换。

`Auto` 与 `DirectDualStack` 保留 TUN 的 IPv4 和 IPv6 捕获地址，通过反转
`default_interface_address: ["2000::/3"]` 条件判断当前默认出口接口，
不按网络类型合并其他接口的地址。默认出口存在 `2000::/3` 公网 IPv6 地址时正常处理 AAAA 和 IPv6 业务分流；
缺少该地址或无法取得接口信息时，AAAA 返回空 `NOERROR`，并在早期及后置守卫拒绝 IPv6。
链路本地地址和 ULA 不满足此条件。该检查不是公网可达性探测：有地址但缺少有效路由、
上游黑洞等情况仍可能失败。已有 DNS 缓存不会随接口变化主动清空，现有连接也不会
因此即时切换；网络切换后的实际行为仍需在目标设备上验证。

[官方字段说明](https://sing-box.sagernet.org/configuration/route/rule/#default_interface_address)
列出 Linux、Windows 和 macOS 支持。Android 使用相同条件，但依赖客户端通过
libbox 正确提供当前物理默认接口和接口地址；不能据此认定所有 Android 图形客户端都支持。
已核对锁定 alpha.12 的接口监视源码并检查生成配置；无法取得默认接口或地址的客户端
会继续拒绝客户端 IPv6。Android 真机和网络切换行为尚未验证。

DIRECT 出口显式绑定 AliDNS，并禁用 optimistic 过期缓存。Auto、DirectDualStack 和 DualStack 下，
其域名拨号使用 `prefer_ipv4`，保留 IPv6 候选；Ipv4Only 下仍为 `ipv4_only`。
此偏好不修改应用收到的 DNS 答案，也不会将已经确定的 IPv6 目标自动转换为 IPv4。
它只在核心以域名进行直连拨号时生效，不能保证 TUN 流量的地址族回退。
代理节点和控制面 HTTP client 的解析策略不受此偏好影响。

控制平面域名由 `route.default_domain_resolver` 解析：直连
`dns-direct-alidns`、随平台地址族选择的策略（`Ipv4Only` 与 `DirectDualStack` 使用 `ipv4_only`，
`Auto` 与 `DualStack` 不强制地址族）、`disable_optimistic_cache = true`。
代理节点和 Tailscale endpoint 继承该设置；rule-set HTTP client 显式绑定相同解析策略。
DIRECT 显式使用上述 `prefer_ipv4`／`ipv4_only` 绑定。Dashboard HTTP client 直连，按平台地址族解析，
`DirectDualStack` 下不强制 IPv4；所有这些绑定均禁用 optimistic 缓存。节点域名不加入 client DNS 规则。
`DirectDualStack` 让节点域名连接和 rule-set 下载等代理控制面不依赖 IPv6，并拒绝 IPv6 字面量代理节点；
`Auto` 不为控制面 resolver 设置 `ipv4_only`，上述条件 AAAA 规则针对 client DNS，
不保证控制面解析和连接自动限制为 IPv4。来源节点显式的 `ip-version: ipv4` 约束
仍会保留。该固定 tag 的 IP 和 TLS 名称可通过 [resolver profile](configuration.md#resolver-profile)
覆盖；连接仍使用 IPv4 字面量直连，引导不依赖系统 DNS 或代理 DNS。

client DNS 依次分类：

1. `Ipv4Only` 下，所有 AAAA 查询返回空 `NOERROR`；`Auto` 与 `DirectDualStack` 下仅在出口缺少公网 IPv6 地址时返回空答。
   有公网 IPv6 出口时，不按 DNS 池静态过滤 AAAA；服务、国内和全局池均正常解析，避免域名不在 `cn` 但真实地址属于 `cnip` 时丢失直连 IPv6。
   IPv6 是否允许转发由路由阶段决定，DNS 查询走代理不等于业务目标走代理。
2. 启用 Tailscale 时，`preferred_by` 命中的 MagicDNS 和 split DNS 交给
   `dns-tailscale`，禁用 optimistic 缓存。
3. `ads` 返回 `NXDOMAIN`。
4. 从有效 profile 的 `route.services` 为每个服务生成独立 pool；默认 AI、Google、GitHub、
   Spotify 和 Games 分别通过自己的服务选择组解析，优先于 `cn`。
   服务顺序与路由一致：`PrecedesDomesticRoutes` 为真的服务在前，各阶段保留列表顺序。
   同时命中多个服务时采用最先匹配的服务；同时命中 `cn` 时仍采用该服务的 DNS。
5. `cn` 进入 DOMESTIC pool。
6. 其余查询进入 GLOBAL pool。

`PrecedesDomesticRoutes` 仅控制路由中服务与国内 UDP/443 特例的相对顺序，
不筛选 DNS 服务分类。国内 UDP/443 保持直连特例。

## Resolver pool 响应语义

DOMESTIC 的首选为直连 AliDNS、备用为直连 Tencent；GLOBAL 的首选为经主代理
Cloudflare、备用为经主代理 Google。每个服务也使用 Cloudflare 和 Google，
但两者的连接出口均为该服务的选择组，跟随服务组的手动切换；选择 DIRECT 时 DNS 也直连。
缓存策略保持不变，切换出口不会自动刷新已有答案；旧答案仍可能在 TTL 或配置的
optimistic 缓存窗口内复用。
按端口或 IP 分流的例外（包括国内 UDP/443）不由域名 DNS 分类决定，仍可能采用不同出口。
解析出口对齐不能保证 CDN 最优；并行模式比较 DNS 响应速度，不探测答案中服务器的连接速度。
DNS 构建器从本次编译的 `ResolvedProfile.Settings` 读取
`DnsSelectionMode`：默认 Android 使用 `SequentialFallback`，Linux/Windows 使用
`ParallelFastest`。

顺序回退模式（默认 Android）先 `evaluate` primary（`timeout: 2s`），随后按顺序判断 primary 的
`NOERROR` 和 `NXDOMAIN`。只有这两个规则都不接受 primary 结果时，才会执行
secondary `evaluate`；secondary 继承全局 `dns.timeout`（默认 `3s`）。健康情况下因此
只产生一个上游请求。两家都不响应时，顺序预算可能累计至约 `2s + 3s`；
这不是整条连接的总超时。

并行模式（默认 Linux/Windows）先连续执行两个 `evaluate`，让 primary 和 secondary 并行查询。
A 和允许放行的 AAAA 查询分别进入独立的地址池，两条 `NOERROR` `respond`
同时要求响应中有对应地址族的地址（A 为 `ip_cidr: 0.0.0.0/0`，AAAA 为 `::/0`）
并启用 `race: true`。地址池禁用 optimistic 缓存，避免过期空答案抢赢。最快含地址的响应
立即提交，空 NOERROR、仅 CNAME 或仅附加区地址不能抢赢。响应条件不重复带
域名 rule-set，避免其与地址条件的 OR 组合绕过检查；唯一的 `match_response`
标签继承前面 evaluate 的分类范围。仅 CNAME 仍可作为后备返回，不被改写为失败。

两条地址竞速均未命中后，依次接受 primary/secondary 的 NOERROR 后备，再依次
接受 primary/secondary 的 NXDOMAIN。地址池以同 scope 的 SERVFAIL 结束，
不会进入后续通用池重复查询。HTTPS、SVCB、MX 等非地址查询保持原有 NOERROR
竞速，不要求响应含 IP；`Ipv4Only` 下 AAAA 在进入池前已返回空答，
`Auto` 下则由前述出口条件决定是否允许进入池。

非地址查询的 NXDOMAIN `respond` 同样不启用 race，等待未决的 NOERROR 竞速。
每次上游查询的默认超时来自 `dns.timeout`，默认 `3s`：快负答或空答与
另一侧不响应的组合仍可能等满该预算。这是保留有效结果优先的代价。较短预算也可能在弱网或首次 DoH 建连时增加失败；
可通过 profile 覆盖 `dns.timeout`，实际延迟与成功率需要真实网络测量。

该预算也覆盖代理拨号与 DoH TLS 握手；默认 `3s` 为代理冷启动留出余量，
并非每次查询固定等待。顺序首选的 `2s` 是独立预算，不随 `dns.timeout` 覆盖而改变。
固定核心在 DoH 交换超时后会重置连接池，不能依赖已超时的连接在后台预热；
超时失败不作为域名负答案缓存，后续查询仍可重新尝试。

每个 pool 最后都有同 scope 的 `predefined SERVFAIL`。双方传输失败、超时、
SERVFAIL 或 REFUSED 时在本 pool 终止，服务和 CN 不会穿透到后一分类。
`dns.final = dns-proxy-cloudflare` 仅为异常兜底，正常 client 查询由各自 pool
结束。

正常 TTL 缓存和 reverse mapping 保留；缓存容量来自 `dns.cache_capacity`
（默认 4096），optimistic 超时来自 `dns.optimistic_timeout`（默认 `6h`）。
并行模式地址池的 A/AAAA evaluate 单独设置
`disable_optimistic_cache: true`，只使用正常 TTL 内的缓存，避免过期空答或旧
地址抢赢新查询；它同时牺牲了地址查询过期正向缓存的立即返回能力。其他查询
仍保留 optimistic。控制平面解析和 Tailscale split DNS 也禁用 optimistic 缓存。

生成校验器检查 `dns.final`、DNS 规则的 `server` 和 `rule_set` 引用。
DNS `preferred_by` 引用 DNS server，而不是路由 outbound/endpoint；当前
模型只有 Tailscale DNS server 支持这种匹配，HTTPS DNS 不支持。
`match_response` 必须引用同一规则列表中已出现的命名 `evaluate`，不允许
向后引用、引用自身或重复定义响应标签。响应标签与 DNS server、rule-set
标签使用独立命名空间，按大小写精确匹配。省略 `dns.final`（空字符串）仍
采用 sing-box 的首个 DNS server 默认值；内置生成策略和规则顺序不变。

<!-- BEGIN GENERATED:core-baseline -->
当前最低支持与资格验证核心为官方 sing-box `1.15.0-alpha.12`，
revision `25b0d5ea3e466d246c708ac0f20d0448110e566c`。版本、revision 与归档摘要统一来自
[核心锁定数据](data/sing-box-core.json)；不代表验证了所有后续版本或定制构建。
<!-- END GENERATED:core-baseline -->

规则语法参见官方
[DNS rule action](https://sing-box.sagernet.org/configuration/dns/rule_action/)、
[DNS rule](https://sing-box.sagernet.org/configuration/dns/rule/) 和
[route default_domain_resolver](https://sing-box.sagernet.org/configuration/route/)。
这些在线文档可能随版本更新；固定 `alpha.8` 源码的协议研究证据与当前
锁定核心检查、本地回环各自的覆盖范围见[协议验证边界](protocol-validation.md)。
当前不生成 `dns_server_address`、`dns_search_domain`、DHCP 或 local
resolver。

[返回 README](../README.md)
