# DNS 与路由优先级

sing-box 规则顺序会直接改变行为，因此 BoxForge 将生成顺序视为可测试的公开约定。
本文记录当前构建器实际输出的顺序，便于修改规则时评估优先级影响。

## 路由规则顺序

`RouteProfileBuilder` 按以下顺序生成顶层规则：

非 Android 平台使用显式阶段扩展 `DIRECT` 规则。私网地址和 `223.5.5.5` 在
首个 `sniff` 前加入 `bridge-out` L3 forwarding；Linux 还先尝试
`auto_redirect` kernel-level `bypass`。需要嗅探或后续优先级才能判定的国内
直连规则保持原位，只在 UDP sniff 后加入 `tun-in` + UDP 限定的 bridge/bypass
伴随规则。每条原 `DIRECT` 都保留为回退，仅针对 `mixed-in` 的直连规则不扩展。

1. 劫持 TUN 和 mixed inbound 的 DNS 流量。
2. 以 `ip_version: 6` 在 Pre-match 阶段拒绝全部 IPv6，包括私网、Tailscale
   和国内 IPv6；这条规则位于任何 DIRECT、bridge 或 bypass 之前。
3. 启用 Tailscale 时，路由 Tailscale endpoint 声明为首选的 IPv4 目标。
4. 直连私网地址和 DoH bootstrap 的 IPv4 地址。
5. 对未被上述 IPv6 总闸门处理的流量，分别嗅探 TCP HTTP/TLS 与 UDP QUIC/STUN。
6. 拒绝识别出的 STUN 协议，然后拒绝 `ads`。
7. mixed inbound 对所有代理服务域名和国内域名执行 `resolve` + `ipv4_only`。
8. 按服务定义顺序拒绝 AI、Google 的 UDP/443，促使 QUIC 回退 TCP；这两条及
   后续 UDP/443 兜底拒绝均设置 `no_drop: true`，不因触发频率切换为静默丢弃。
9. 再次通过后置 `ip_version: 6` 拒绝 IPv6，覆盖 mixed/domain 解析等非
   Pre-match 路径。
10. 将 AI 路由到 `AI`，再将 Google 路由到 `Google`。它们位于后置 IPv6
   拒绝规则之后，因此 IPv6 地址不会进入代理出站。
11. 放行国内域名的 UDP/443；mixed inbound 先以 `ipv4_only` 解析目标后再放行 `cnip`，
   其他 UDP/443 全部拒绝。
12. 生成其他服务分流，当前为 Spotify、Games 和 Microsoft。
13. 直连 `cn`；mixed inbound 对剩余目标执行
    `resolve` + `ipv4_only`，解析后先复检并直连私网地址，再按 `cnip`
    直连。
14. 未命中规则的流量使用主代理组。

第 11、13 步中的国内 `DIRECT` 在 Linux/Windows 会紧邻原规则之前生成 L3
伴随规则：Windows 为 bridge，Linux 为 bypass 后接 bridge。伴随规则仅匹配
`tun-in` UDP，原规则的 rule-set、端口与服务顺序条件不变；TCP 在 sniff
后不会尝试这些 L3 层，仍由原 `DIRECT` 处理。Android 不生成伴随规则。

对业务分流而言，核心优先级是：

```text
IPv6 早期拒绝 → 广告拒绝 → IPv4-only 域名解析 → IPv6 兜底拒绝 → 代理服务 → 国内 IPv4 → 最终代理
```

代理服务的 `ipv4_only` 解析规则必须位于国内域名解析之前，因为
`cn` 可能与 Google 等业务 rule-set 相交。所有代理服务路由则必须
位于后置 IPv6 拒绝之后，确保服务分流只处理 IPv4 目标。

## DNS Architecture v2

`AddressFamilyPolicies` 为 Linux、Windows、Android 统一选择 `Ipv4Only`。
它同时生成 `dns.strategy = ipv4_only`、最前面的全局 AAAA 空 `NOERROR`，
以及 route 的早期和解析后 IPv6 reject。保留 `DualStack` 策略入口，但当前
没有平台使用它。路由 reject 仍覆盖字面量 IPv6 和不经 sing-box DNS 的连接。

控制平面域名由 `route.default_domain_resolver` 解析：直连
`dns-direct-alidns`、`ipv4_only`、`disable_optimistic_cache = true`。
代理节点、直连出站和 Tailscale endpoint 继承该设置；rule-set 和 Dashboard
HTTP client 显式使用相同 resolver。节点域名不加入 client DNS 规则。

client DNS 依次分类：

1. 所有 AAAA 查询返回空 `NOERROR`。
2. 启用 Tailscale 时，`preferred_by` 命中的 MagicDNS 和 split DNS 交给
   `dns-tailscale`，禁用 optimistic 缓存。
3. `ads` 返回 `NXDOMAIN`。
4. 从 `ProfileDefinitions.Services` 中所有 `PrecedesDomesticRoutes` 服务导出
   rule-set；当前 AI 和 Google 进入 GLOBAL pool，优先于 `cn`。
5. `cn` 进入 DOMESTIC pool。
6. 其余查询进入 GLOBAL pool。

## Resolver pool 响应语义

DOMESTIC 的首选为直连 AliDNS、备用为直连 Tencent；GLOBAL 的首选为经主代理
Cloudflare、备用为经主代理 Google。`DnsResolverSelectionPolicies` 按平台选择
pool compiler：Android 使用 `SequentialFallback`，Linux/Windows 使用
`ParallelFastest`；`ParallelPrimaryPreferred` 保留为明确的非默认策略。

Android 先 `evaluate` primary（`timeout: 2s`），随后按顺序判断 primary 的
`NOERROR` 和 `NXDOMAIN`。只有这两个规则都不接受 primary 结果时，才会执行
secondary `evaluate`；secondary 继承全局 `dns.timeout = 5s`。健康情况下因此
只产生一个上游请求。

Linux/Windows 先连续执行两个 `evaluate`，让 primary 和 secondary 并行查询。
两条 `NOERROR` `respond` 启用 `race: true`，最快有效答案立即提交并取消其余
查询。primary、secondary 的 NXDOMAIN `respond` 不启用 race；它们会被未决的
NOERROR race 阻挡，直到两条正向竞速均未命中，再按 primary、secondary 顺序
判断。因此快速 NXDOMAIN 不会压掉稍慢的 NOERROR，双方均 NXDOMAIN 时仍返回
primary NXDOMAIN。

每个 pool 最后都有同 scope 的 `predefined SERVFAIL`。双方传输失败、超时、
SERVFAIL 或 REFUSED 时在本 pool 终止，priority 和 CN 不会穿透到后一分类。
`dns.final = dns-proxy-cloudflare` 仅为异常兜底，正常 client 查询由各自 pool
结束。

正常 TTL 缓存和容量 4096 的 reverse mapping 保留；optimistic 缓存超时为
`6h`。控制平面解析和 Tailscale split DNS 禁用 optimistic 缓存。

上述规则依据 sing-box 1.15.0-alpha.8 的
[DNS rule action](https://sing-box.sagernet.org/configuration/dns/rule_action/)、
[DNS rule](https://sing-box.sagernet.org/configuration/dns/rule/) 和
[route default_domain_resolver](https://sing-box.sagernet.org/configuration/route/)。
本阶段不生成 `dns_server_address`、`dns_search_domain`、DHCP 或 local
resolver。

[返回 README](../README.md)
