# DNS 与路由优先级

sing-box 规则顺序会直接改变行为，因此 BoxForge 将生成顺序视为可测试的公开约定。
本文记录内置 profile 的默认输出顺序，便于修改规则时评估优先级影响。
平台参数、服务及策略可通过[统一 profile](configuration.md#统一-profile)覆盖；阶段顺序保持不变。

## 路由规则顺序

以下按 `RoutePlan` 的阶段列出实际执行顺序，由 `SingboxRouteEmitter` 展开为目标规则。修改路由规则时，
需同步维护本节说明。IPv6 守卫仅在 `Ipv4Only` 下生成，Tailscale 阶段仅在
对应平台启用 Tailscale 时生成规则；最终出口由 `route.final` 指定。

仅 Linux 使用显式阶段扩展 `DIRECT` 规则。私网地址和主 DoH bootstrap 地址（默认 `223.5.5.5`）在
首个 `sniff` 前加入 `bridge-out` L3 forwarding；Linux 还先尝试
`auto_redirect` kernel-level `bypass`。需要嗅探或后续优先级才能判定的国内
直连规则保持原位，只在 UDP sniff 后加入 `tun-in` + UDP 限定的 bridge/bypass
伴随规则。每条原 `DIRECT` 都保留为回退。

1. **`DnsHijack`**：劫持 TUN inbound 的 DNS 流量。
2. **`EarlyIpv6Guard`**（在 `DnsHijack` 之后）：Ipv4Only 时，以全局 ip_version: 6 拒绝全部 IPv6，先于 sniff、DIRECT、bridge 和 bypass。
3. **`Tailscale`**（在 `EarlyIpv6Guard` 之后）：启用 Tailscale 时，路由 endpoint 声明为首选的目标，优先于私网直连。
4. **`PreSniffDirect`**（在 `Tailscale` 之后）：直连私网地址和有效 profile 的 `alidns.server`（默认 223.5.5.5）；Linux 在每条 DIRECT 前依次展开 bypass、bridge。
5. **`Sniff`**（在 `PreSniffDirect` 之后）：依次嗅探 TCP HTTP/TLS 与 UDP QUIC/STUN；TCP 跳过 FTP、SSH、MySQL、VNC 常用端口 21/22/3306/5900，继续后续路由，8080/8443 仍嗅探。
6. **`BlockStunAndAds`**（在 `Sniff` 之后）：依次拒绝识别出的 UDP STUN 和 ads。
7. **`PriorityUdp443Reject`**（在 `BlockStunAndAds` 之后）：按服务定义顺序拒绝 🤖 AI、🔎 Google 的 UDP/443，设置 no_drop: true，促使 QUIC 回退 TCP。
8. **`LateIpv6Guard`**（在 `PriorityUdp443Reject` 之后）：Ipv4Only 时，保留独立的全局 ip_version: 6 后置拒绝，先于所有代理服务路由。
9. **`PriorityServices`**（在 `LateIpv6Guard` 之后）：按服务定义顺序路由 🤖 AI、🔎 Google，优先于国内 UDP/443 例外。
10. **`DomesticUdp443`**（在 `PriorityServices` 之后）：依次放行 cn、cnip 的 UDP/443，再以 no_drop: true 拒绝其他 UDP/443；Linux 的 DIRECT 伴随规则仅匹配 TUN UDP。
11. **`OtherServices`**（在 `DomesticUdp443` 之后）：按服务定义顺序生成其他服务分流：🎵 Spotify、🎮 GAMES、🪟 Microsoft。
12. **`DomesticDirect`**（在 `OtherServices` 之后）：依次按 cn、cnip 直连国内目标；Linux 的 DIRECT 伴随规则仅匹配 TUN UDP，TCP 仍由原 DIRECT 处理。
13. **`FinalOutbound`**（在 `DomesticDirect` 之后）：未命中规则的流量使用 route.final 的 🚀 PROXIES；不额外生成 catch-all 规则。

`DomesticUdp443`、`DomesticDirect` 阶段中的国内 `DIRECT` 在 Linux 会紧邻原规则之前生成 L3
伴随规则：bypass 后接 bridge。伴随规则仅匹配
`tun-in` UDP，原规则的 rule-set、端口与服务顺序条件不变；TCP 在 sniff
后不会尝试这些 L3 层，仍由原 `DIRECT` 处理。Windows 和 Android 不生成伴随规则，使用原有 `DIRECT`。

全平台仅生成 TUN inbound；不再生成 mixed 入口、其专用的域名 `resolve`、
解析后 Tailscale 复检或私网回退规则。TUN 原有的路由顺序保持不变。

对业务分流而言，核心优先级是：

```text
IPv6 早期拒绝 → 广告拒绝 → IPv6 兜底拒绝 → 代理服务 → 国内 IPv4 → 最终代理
```

`Ipv4Only` 下，所有代理服务路由位于后置 IPv6 拒绝之后，确保服务分流只处理 IPv4 目标。

## DNS 分类与控制面解析

有效 `BoxForgeProfile` 中各平台的 `PlatformProfile.AddressFamily` 决定地址族；
内置 JSON 为 Linux、Windows、Android 均设置 `Ipv4Only`。此模式生成
`dns.strategy = ipv4_only`、最前面的全局 AAAA 空 `NOERROR`，以及 route 的早期和
后置 IPv6 reject，后者也覆盖字面量 IPv6 和不经 sing-box DNS 的连接。
通过 `platforms.<平台>.address_family` 可覆盖为 `DualStack`，此时不生成上述
AAAA 阻断和 IPv6 reject，DNS 与控制面解析策略也随之切换；内置默认值没有启用双栈。

控制平面域名由 `route.default_domain_resolver` 解析：直连
`dns-direct-alidns`、随平台地址族选择的策略（默认 `ipv4_only`）、
`disable_optimistic_cache = true`。
代理节点、直连出站和 Tailscale endpoint 继承该设置；rule-set 和 Dashboard
HTTP client 显式使用相同 resolver。节点域名不加入 client DNS 规则。
该固定 tag 的 IP 和 TLS 名称可通过 [resolver profile](configuration.md#resolver-profile)
覆盖；连接仍使用 IPv4 字面量直连，引导不依赖系统 DNS 或代理 DNS。

client DNS 依次分类：

1. `Ipv4Only` 下，所有 AAAA 查询返回空 `NOERROR`。
2. 启用 Tailscale 时，`preferred_by` 命中的 MagicDNS 和 split DNS 交给
   `dns-tailscale`，禁用 optimistic 缓存。
3. `ads` 返回 `NXDOMAIN`。
4. 从有效 `BoxForgeProfile.Services` 中所有服务导出 rule-set；默认 AI、Google、
   Spotify、Games 和 Microsoft 进入 GLOBAL pool，优先于 `cn`。
   与服务同时命中 `cn` 的域名也使用代理 DNS，保持与普通业务路由的服务优先级一致。
5. `cn` 进入 DOMESTIC pool。
6. 其余查询进入 GLOBAL pool。

`PrecedesDomesticRoutes` 仅控制路由中服务与国内 UDP/443 特例的相对顺序，
不再用于筛选 DNS 服务分类。国内 UDP/443 的原有直连特例保持不变。

## Resolver pool 响应语义

DOMESTIC 的首选为直连 AliDNS、备用为直连 Tencent；GLOBAL 的首选为经主代理
Cloudflare、备用为经主代理 Google。DNS 构建器从注入的 `BoxForgeProfile` 读取
平台的 `DnsSelectionMode`：默认 Android 使用 `SequentialFallback`，Linux/Windows 使用
`ParallelFastest`。

顺序回退模式（默认 Android）先 `evaluate` primary（`timeout: 2s`），随后按顺序判断 primary 的
`NOERROR` 和 `NXDOMAIN`。只有这两个规则都不接受 primary 结果时，才会执行
secondary `evaluate`；secondary 继承全局 `dns.timeout`（默认 `5s`）。健康情况下因此
只产生一个上游请求。

并行模式（默认 Linux/Windows）先连续执行两个 `evaluate`，让 primary 和 secondary 并行查询。
A 查询先进入独立的地址池，两条 `NOERROR` `respond` 同时要求响应中有
IPv4 地址（`ip_cidr: 0.0.0.0/0`）并启用 `race: true`。最快含地址的响应
立即提交，空 NOERROR、仅 CNAME 或仅附加区地址不能抢赢。响应条件不重复带
域名 rule-set，避免其与地址条件的 OR 组合绕过检查；唯一的 `match_response`
标签继承前面 evaluate 的分类范围。仅 CNAME 仍可作为后备返回，不被改写为失败。

两条地址竞速均未命中后，依次接受 primary/secondary 的 NOERROR 后备，再依次
接受 primary/secondary 的 NXDOMAIN。地址池以同 scope 的 SERVFAIL 结束，
不会进入后续通用池重复查询。HTTPS、SVCB、MX 等非 A 查询保持原有 NOERROR
竞速，不要求响应含 IP；`Ipv4Only` 下 AAAA 在进入池前已返回空答。

非 A 查询的 NXDOMAIN `respond` 同样不启用 race，等待未决的 NOERROR 竞速。
所有查询的全局超时来自 `policies.dns_timeout`，默认 `5s`：快负答或空答与
另一侧不响应的组合仍可能等满该预算。这是保留有效结果优先的代价。

每个 pool 最后都有同 scope 的 `predefined SERVFAIL`。双方传输失败、超时、
SERVFAIL 或 REFUSED 时在本 pool 终止，priority 和 CN 不会穿透到后一分类。
`dns.final = dns-proxy-cloudflare` 仅为异常兜底，正常 client 查询由各自 pool
结束。

正常 TTL 缓存和 reverse mapping 保留；缓存容量来自 `policies.dns_cache_capacity`
（默认 4096），optimistic 超时来自 `policies.dns_optimistic_timeout`（默认 `6h`）。
并行模式地址池的 A evaluate 单独设置
`disable_optimistic_cache: true`，只使用正常 TTL 内的缓存，避免过期空答或旧
地址抢赢新查询；它同时牺牲了 A 查询过期正向缓存的立即返回能力。其他查询
仍保留 optimistic。控制平面解析和 Tailscale split DNS 也禁用 optimistic 缓存。

生成校验器检查 `dns.final`、DNS 规则的 `server` 和 `rule_set` 引用。
DNS `preferred_by` 引用 DNS server，而不是路由 outbound/endpoint；当前
模型只有 Tailscale DNS server 支持这种匹配，HTTPS DNS 不支持。
`match_response` 必须引用同一规则列表中已出现的命名 `evaluate`，不允许
向后引用、引用自身或重复定义响应标签。响应标签与 DNS server、rule-set
标签使用独立命名空间，按大小写精确匹配。省略 `dns.final`（空字符串）仍
采用 sing-box 的首个 DNS server 默认值；内置生成策略和规则顺序不变。

当前最低支持与资格验证核心为官方 sing-box `1.15.0-alpha.12`，revision
`25b0d5ea3e466d246c708ac0f20d0448110e566c`。规则语法参见官方
[DNS rule action](https://sing-box.sagernet.org/configuration/dns/rule_action/)、
[DNS rule](https://sing-box.sagernet.org/configuration/dns/rule/) 和
[route default_domain_resolver](https://sing-box.sagernet.org/configuration/route/)。
这些在线文档可能随版本更新；固定 `alpha.8` 源码的协议研究证据与当前
`alpha.12` 核心检查、本地回环各自的覆盖范围见[协议验证边界](protocol-validation.md)。
当前不生成 `dns_server_address`、`dns_search_domain`、DHCP 或 local
resolver。

[返回 README](../README.md)
