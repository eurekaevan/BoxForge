# DNS 与路由优先级

sing-box 规则顺序会直接改变行为，因此 BoxForge 将生成顺序视为可测试的公开约定。
本文记录内置 profile 的默认输出顺序，便于修改规则时评估优先级影响。
平台参数、服务及策略可通过[统一 profile](configuration.md#统一-profile)覆盖；阶段顺序保持不变。

## 路由规则顺序

以下按 `SingboxRouteEmitter` 固定展开的阶段列出实际执行顺序。修改路由规则时，
需同步维护本节说明。IPv6 守卫在 `Ipv4Only` 下无条件生效，在 `Auto` 与 `DirectDualStack` 下按出口接口状态生效，
`DirectDualStack` 另在代理服务和最终出口之前拒绝 IPv6；Tailscale 阶段仅在
对应平台启用 Tailscale 时生成规则；最终出口由 `route.final` 指定。

启用 `route.use_direct_forwarding` 时使用显式阶段扩展 `DIRECT` 规则，默认仅 Linux 启用。
私网地址在首个 `sniff` 前加入 `bridge-out` L3 forwarding；同时启用
`auto_redirect: true` 时，同一伴随规则使用 `bypass` 动作，auto_redirect 流量从内核层直接绕过，普通 TUN 流量仍由 bridge 转发。需要嗅探或后续优先级才能判定的国内
直连规则保持原位，只在 UDP sniff 后加入 `tun-in` + UDP 限定的 bridge/bypass
伴随规则。每条原 `DIRECT` 都保留为回退。

1. **`DnsHijack`**：以 `inbound: tun-in` 和 `port: 53` 兜底劫持 TCP/UDP DNS；主要入口是 TUN 自身的 `dns_mode: hijack`，不依赖后续 sniff 的协议识别。
2. **`Ipv4OnlyGuard`**（在 `DnsHijack` 之后）：仅 `Ipv4Only` 生成，以全局 `ip_version: 6` 拒绝全部 IPv6，先于 Tailscale、DIRECT、bridge、bypass 和 sniff。
3. **`Tailscale`**（在 `Ipv4OnlyGuard` 之后）：启用 Tailscale 时，路由 endpoint 声明为首选的目标，优先于私网直连。
4. **`PreSniffDirect`**（在 `Tailscale` 之后）：直连私网地址；启用直连转发时在 DIRECT 前展开 bridge 伴随规则，同时启用 auto_redirect 时将其动作改为 bypass。
5. **`InterfaceIpv6Guard`**（在 `PreSniffDirect` 之后）：仅 `Auto` 与 `DirectDualStack` 生成，在默认出口缺少公网 IPv6 地址时拒绝尚未处理的 IPv6；Tailscale 和私网 IPv6 不受此条件限制。
6. **`Sniff`**（在 `InterfaceIpv6Guard` 之后）：依次嗅探 TCP HTTP/TLS 与 UDP QUIC/STUN；TCP 跳过 FTP、SSH、MySQL、VNC 常用端口 21/22/3306/5900，继续后续路由，8080/8443 仍嗅探。
7. **`BlockStunAndAds`**（在 `Sniff` 之后）：依次拒绝识别出的 UDP STUN 和 ads。
8. **`ServiceIpv6Guard`**（在 `BlockStunAndAds` 之后）：仅 `DirectDualStack` 生成。仅对优先服务的规则集匹配 `ip_version: 6` 并拒绝。
9. **`PriorityServices`**（在 `ServiceIpv6Guard` 之后）：按列表顺序路由 `precedes_domestic_routes: true` 的服务；该字段默认 `true`，内置 AI、Google、GitHub、Spotify、Games 均在此阶段。
10. **`DomesticDirect`**（在 `PriorityServices` 之后）：依次按 cn、cnip 直连国内目标，TCP、UDP 的所有端口使用相同优先级；启用直连转发时的 DIRECT 伴随规则仅匹配 TUN UDP，TCP 仍由原 DIRECT 处理。
11. **`OtherServiceIpv6Guard`**（在 `DomesticDirect` 之后）：仅 `DirectDualStack` 且存在 `false` 服务时生成，对未先命中国内直连的普通服务规则集拒绝 IPv6。
12. **`OtherServices`**（在 `OtherServiceIpv6Guard` 之后）：按列表顺序路由显式设置 `precedes_domestic_routes: false` 的服务；默认没有此阶段。
13. **`FinalIpv6Guard`**（在 `OtherServices` 之后）：仅 `DirectDualStack` 生成，以全局 `ip_version: 6` 拒绝所有未被前面规则直连的 IPv6，保证没有 IPv6 进入最终代理出口。
14. **`FinalOutbound`**（在 `FinalIpv6Guard` 之后）：未命中规则的流量使用 route.final 的 🚀 PROXIES；不额外生成 catch-all 规则。

`DomesticDirect` 阶段中的国内 `DIRECT` 在启用直连转发时会紧邻原规则之前生成 L3
伴随规则：同时启用 auto_redirect 时使用 bypass，否则使用 route；两者均指定
`preferred_by: bridge-out` 和 `outbound: bridge-out`。伴随规则仅匹配
`tun-in` UDP，原规则的 rule-set、端口与服务顺序条件不变；TCP 在 sniff
后不会尝试这些 L3 层，仍由原 `DIRECT` 处理。关闭或清空 auto_redirect 不会关闭 bridge，
关闭直连转发时仅保留原 `DIRECT`。bridge 支持 Linux、root Android 和需要 WinDivert/管理员权限的
Windows x86/x64；auto_redirect 只支持 Linux 与 root Android，普通 Android VPN 客户端不支持，Windows 启用时校验拒绝。
DIRECT 是匹配阶段的回退，不会在 bridge 初始化或转发失败后自动重试。

全平台仅生成 TUN inbound，不生成 mixed 入口及其专用的域名解析和解析后复检规则。

嗅探覆盖提前放行路径之外的大多数 TUN TCP 和 UDP，发生在业务分流之前。
`route.sniff_timeout` 默认为 `300ms`，表示嗅探的超时预算，并非每条连接固定增加
300ms；能快速识别或结束嗅探的连接可提前继续，等待数据或识别的连接则可能增加首包等待。
该预算也不代表连接建立的总耗时上限。参见官方 [sniff 说明](https://sing-box.sagernet.org/configuration/route/rule_action/#sniff)。

可通过统一 profile 调整 `route.sniff_timeout`；`route.sniff_excluded_ports` 仅排除 TCP
目标端口，不影响 UDP QUIC/STUN 嗅探。缩短预算或增加排除端口可能降低协议和域名识别率，
进而影响依赖嗅探结果的分流；仅保留常见 Web 端口也会遗漏非标准端口上的 HTTP/TLS。
评估调整时应分别测量快速识别、无法识别和等待服务端先发数据的连接，不能从配置值推导固定延迟。
国内公网 IP（`cnip`）也保持在嗅探之后直连，以先完成 STUN/广告拒绝和相应的服务优先级匹配；
普通 UDP 数据包可在嗅探器判定不匹配后立即继续，分片 QUIC 等需要更多数据时才可能继续等待。

对业务分流而言，核心优先级是：

```text
IPv6 早期拒绝 → 广告拒绝 → 优先服务 → 国内 IPv4 → 普通服务 → 最终代理
```

`Ipv4Only` 下，单道 IPv6 拒绝位于 Tailscale 和全部直连及代理路由之前，确保业务流量只处理 IPv4 目标。
`DirectDualStack` 下，各服务类别在自己的阶段拒绝 IPv6；出口条件允许且未先命中优先服务时，国内 `cn`/`cnip` 的 IPv6 直连，
即使同时命中 `false` 服务也先直连；
其余 IPv6 在最终出口之前被拒绝，核心优先级为：

```text
Tailscale → 私网直连 → 接口 IPv6 拒绝 → 广告拒绝 → 优先服务 IPv6 拒绝及分流 → 国内直连 → 普通服务 IPv6 拒绝及分流 → 其余 IPv6 拒绝 → 最终代理
```

地址族限制采用固定路由类别，不跟随 selector 最终叶子的运行时切换。将服务组或主组切到 DIRECT，不会解除该类别的 IPv6 限制。要放开该类别需更改 profile 策略；官方核心不提供按最终叶子匹配的路由条件。

## DNS 分类与控制面解析

每个目标最终配置中的 `dns.address_family` 决定地址族；
内置 JSON 为 Linux、Windows、Android 均设置 `Ipv4Only`（全局 IPv4）。该策略生成
`dns.strategy = ipv4_only`、最前面的全局 AAAA 空 `NOERROR`，以及 DNS 劫持之后的单道
IPv6 reject；该守卫也覆盖字面量 IPv6 和不经 sing-box DNS 的连接，直连与代理流量都不使用 IPv6。
通过 `platforms.<平台>.dns.address_family` 可覆盖为 `DirectDualStack`（代理仅 IPv4、直连保留 IPv6）、
`Auto` 或 `DualStack`。`DualStack` 不生成上述
AAAA 阻断和 IPv6 reject，DNS 与控制面解析策略也随之切换。

`Auto` 与 `DirectDualStack` 保留 TUN 的 IPv4 和 IPv6 捕获地址，通过反转
`default_interface_address: ["2000::/3"]` 条件判断当前默认出口接口，
不按网络类型合并其他接口的地址。默认出口存在 `2000::/3` 公网 IPv6 地址时正常处理 AAAA 和 IPv6 业务分流；
缺少该地址或无法取得接口信息时，非 Tailscale 查询的 AAAA 返回空 `NOERROR`，
路由在 Tailscale 和私网直连之后、sniff 之前拒绝其余 IPv6。
链路本地地址和 ULA 不满足公网出口条件，但私网和 Tailscale IPv6 不依赖该条件放行。
该检查不是公网可达性探测：有地址但缺少有效路由、
上游黑洞等情况仍可能失败。已有 DNS 缓存不会随接口变化主动清空，现有连接也不会
因此即时切换；网络切换后的实际行为仍需在目标设备上验证。

[官方字段说明](https://sing-box.sagernet.org/configuration/route/rule/#default_interface_address)
列出 Linux、Windows 和 macOS 支持。Android 使用相同条件，但依赖客户端通过
libbox 正确提供当前物理默认接口和接口地址；不能据此认定所有 Android 图形客户端都支持。
已核对锁定 alpha.12 的接口监视源码并检查生成配置；无法取得默认接口或地址的客户端
会继续拒绝未被 Tailscale 或私网直连处理的客户端 IPv6。Android 真机和网络切换行为尚未验证。

DIRECT 出口显式绑定 AliDNS，并禁用 optimistic 过期缓存。Auto、DirectDualStack 和 DualStack 下，
其域名拨号使用 `prefer_ipv4`，保留 IPv6 候选；Ipv4Only 下仍为 `ipv4_only`。
此偏好不修改应用收到的 DNS 答案，也不会将已经确定的 IPv6 目标自动转换为 IPv4。
它只在核心以域名进行直连拨号时生效，不能保证 TUN 流量的地址族回退。
代理节点和控制面 HTTP client 的解析策略不受此偏好影响。

控制平面域名由 `route.default_domain_resolver` 解析：直连
`dns-direct-alidns`、随平台地址族选择的策略（`Ipv4Only`、`Auto` 与 `DirectDualStack` 使用 `ipv4_only`，
仅 `DualStack` 不强制地址族）、`disable_optimistic_cache = true`。
代理节点和 Tailscale endpoint 继承该设置；rule-set HTTP client 显式绑定相同解析策略。
DIRECT 显式使用上述 `prefer_ipv4`／`ipv4_only` 绑定。Dashboard HTTP client 直连，按平台地址族解析，
`Auto`、`DirectDualStack` 下不强制 IPv4；所有这些绑定均禁用 optimistic 缓存。节点域名不加入 client DNS 规则。
`Auto` 与 `DirectDualStack` 让节点域名连接和 rule-set 下载等代理控制面不依赖 IPv6，并拒绝 IPv6 字面量代理节点。
上述条件 AAAA 规则仍只针对 client DNS；`Auto` 在出口条件允许时可通过 IPv4 代理节点访问客户端 IPv6 目标，
`DirectDualStack` 则额外拒绝代理类别的客户端 IPv6。来源节点显式的 `ip-version: ipv4` 约束
仅在 `DualStack` 下使用独立节点 resolver，在 `Ipv4Only`、`Auto`、`DirectDualStack` 下继承全局控制面 resolver。
该固定 tag 的 IP 和 TLS 名称可通过 [resolver profile](configuration.md#resolver-profile)
覆盖；连接仍使用 IPv4 字面量直连，引导不依赖系统 DNS 或代理 DNS。
内部国内 DoH 由各自 dialer 直接建立连接，不经过 TUN 客户端路由链；
解析器地址不生成客户端直连例外，应用访问这些地址仍按普通路由规则处理。

client DNS 依次分类：

1. 仅 `Ipv4Only` 下，所有 AAAA 查询返回空 `NOERROR`，包括 Tailscale 查询。
2. 启用 Tailscale 时，`preferred_by` 命中的 MagicDNS 和 split DNS 交给
   `dns-tailscale`，禁用 optimistic 缓存；`Auto` 与 `DirectDualStack` 下先于条件 AAAA 空答规则。
3. `Auto` 与 `DirectDualStack` 下，出口缺少公网 IPv6 地址时，其余 AAAA 查询返回空 `NOERROR`。
   有公网 IPv6 出口时，不按 DNS 池静态过滤 AAAA；服务、国内和全局池均正常解析，避免域名不在 `cn` 但真实地址属于 `cnip` 时丢失直连 IPv6。
   IPv6 是否允许转发由路由阶段决定，DNS 查询走代理不等于业务目标走代理。
4. `ads` 返回 `NXDOMAIN`。
5. 从有效 profile 的 `route.services` 为每个服务生成独立 pool，先处理 `PrecedesDomesticRoutes` 为真的服务；
   该字段默认 `true`，内置 AI、Google、GitHub、Spotify 和 Games 均通过自己的服务选择组解析，优先于 `cn`。
6. `cn` 进入 DOMESTIC pool。
7. 处理 `PrecedesDomesticRoutes` 为假的普通服务 pool；同时命中 `cn` 的域名已在前一步进入 DOMESTIC。
8. 其余查询进入 GLOBAL pool。

DNS 与业务路由均按优先服务 → 国内 → 普通服务排序，各服务阶段保留列表顺序；
同时命中多个同阶段服务时采用最先匹配的服务，TCP、UDP 的所有端口使用相同优先级。

## Resolver pool 响应语义

DOMESTIC 的首选为直连 AliDNS `223.5.5.5`、备用为直连 AliDNS `223.6.6.6`，
TLS 名称均为 `dns.alidns.com`；主备属于同一服务商，不提供跨服务商冗余。
备用使用 resolver ID `alidns-secondary` 和 tag `dns-direct-alidns-secondary`。
GLOBAL 的首选为经主代理
Cloudflare、备用为经主代理 Google。每个服务也使用 Cloudflare 和 Google，
但两者的连接出口均为该服务的选择组，跟随服务组的手动切换；选择 DIRECT 时 DNS 也直连。
运行时手动切换出口不会自动刷新已有答案，旧答案仍可能在正常 TTL 内复用；
重新生成时 DNS 段发生变化会更换持久缓存命名空间，详见[缓存身份](generated-config.md#dns-与持久化缓存)。
仅按 `cnip` 命中的目标无法在 DNS 查询前确定 IP 归属；DNS 按域名分类，路由还会匹配目标 IP，
因此不保证 DNS 查询出口总与业务出口相同。
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
立即提交，空 NOERROR、仅 CNAME 或仅附加区地址不能抢赢。所有 `respond`（包括非竞速后备）
均通过唯一的 `match_response` 标签继承前面 evaluate 的分类范围，不重复携带 rule-set；
地址竞速的 IP 条件也因此不会与域名 rule-set 形成 OR 组合。evaluate 和终止 SERVFAIL 仍保留范围条件。
仅 CNAME 仍可作为后备返回，不被改写为失败。

两条地址竞速均未命中后，依次接受 primary/secondary 的 NOERROR 后备，再依次
接受 primary/secondary 的 NXDOMAIN。地址池以同 scope 的 SERVFAIL 结束，
不会进入后续通用池重复查询。HTTPS、SVCB、MX 等非地址查询保持原有 NOERROR
竞速，不要求响应含 IP；`Ipv4Only` 下 AAAA 在进入池前已返回空答，
`Auto` 下则由前述出口条件决定是否允许进入池。

通用池不限制查询类型，TXT、PTR、SRV 和其他类型也按相同规则处理。
非地址查询的空 `NOERROR` 是有效结果，可以先于另一侧的非空答案返回；不会因此请求其他分类。
锁定核心在 `Ipv4Only` 下会移除 HTTPS 答案中的 `ipv6hint`，保留 ALPN 和 `ipv4hint`；
`DualStack` 下保留两类地址提示。

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
（默认 4096）。所有解析池的 evaluate 均设置 `disable_optimistic_cache: true`，
覆盖所有查询类型以及顺序、并行两种模式，避免过期空答、NXDOMAIN 或旧地址参与响应选择。
缓存过期后需要等待上游，无法立即使用旧答案兜底；正常 TTL 内的正答和负答仍可复用。
全局 optimistic 超时仍来自 `dns.optimistic_timeout`（默认 `6h`），为兼容已有 profile 保留输出，
正常客户端查询在解析池中覆盖禁用；控制平面解析和 Tailscale split DNS 也禁用 optimistic 缓存。

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
