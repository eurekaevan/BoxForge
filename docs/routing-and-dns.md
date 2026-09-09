# DNS 与路由优先级

sing-box 规则顺序会直接改变行为，因此 BoxForge 将生成顺序视为可测试的公开约定。
本文记录当前构建器实际输出的顺序，便于修改规则时评估优先级影响。

## 路由规则顺序

`RouteProfileBuilder` 按以下顺序生成顶层规则：

在首个 `sniff` 之前，私网地址和 `223.5.5.5` 两条 `DIRECT` 规则会在非
Android 平台先尝试 `bridge-out` L3 forwarding，再以原 `DIRECT` 作为 L4
回退；Linux 还会更早尝试 `auto_redirect` kernel-level `bypass`。这些层仅限
`tun-in` 并复用原目标条件。依赖嗅探或后续服务优先级的国内直连规则，以及
仅针对 `mixed-in` 的直连规则，不会提前或添加无效的 L3/bypass 层。

1. 劫持 TUN 和 mixed inbound 的 DNS 流量。
2. 启用 Tailscale 时，先路由 Tailscale endpoint 声明为首选的目标。
3. 直连私网地址和 DoH bootstrap 的 IP 地址。
4. 拒绝固定 STUN UDP 端口，然后分别嗅探 TCP HTTP/TLS 与 UDP QUIC。
5. 拒绝 anti-AD 和 `geosite-category-ads-all`。
6. mixed inbound 对所有代理服务域名执行 `resolve` + `ipv4_only`；对国内域名
   执行 `resolve` + `prefer_ipv4`，以便后续按实际 IP 执行 IPv6 总闸门。
7. 按服务定义顺序拒绝 AI、Google 的 UDP/443，促使 QUIC 回退 TCP。
8. 仅直连命中 `geoip-cn` 的公网 IPv6，然后拒绝其他公网 IPv6。
9. 将 AI 路由到 `AI`，再将 Google 路由到 `Google`。它们位于公网 IPv6
   拒绝规则之后，因此即使应用直接提供 IPv6 地址也不会经代理出站。
10. 放行国内域名的 UDP/443；mixed inbound 先以 `ipv4_only` 解析目标后再放行 `geoip-cn`，
   其他 UDP/443 全部拒绝。
11. 生成其他服务分流，当前为 Spotify、Steam 和 Microsoft。
12. 直连 `geosite-cn`/`geosite-category-pt`；mixed inbound 对剩余目标执行
    `resolve` + `ipv4_only`，解析后先复检并直连私网地址，再按 `geoip-cn`
    直连。
13. 未命中规则的流量使用主代理组。

对业务分流而言，核心优先级是：

```text
广告拒绝 → 域名解析 → 国内 IPv6 / 其他 IPv6 拒绝 → 代理服务 → 国内 IPv4 → 最终代理
```

代理服务的 `ipv4_only` 解析规则必须位于国内域名解析之前，因为
`geosite-cn` 可能与 Google 等业务 rule-set 相交。所有代理服务路由则必须
位于公网 IPv6 拒绝之后，确保服务分流只处理 IPv4 目标。

## DNS 规则顺序

`DnsProfileBuilder` 的顶层顺序是：

1. 启用 Tailscale 时，将 MagicDNS 和分流后缀交给 Tailscale DNS，并禁用
   optimistic 过期缓存。
2. 普通 DNS 查询命中代理节点域名时，使用专用本地解析器，仅请求 A 记录，
   并禁用 optimistic 过期缓存。代理出站解析自己的服务器域名时不会经过这条
   规则，而由各出站的 `domain_resolver` 独立施加同样约束。
3. 广告域名直接返回 `NXDOMAIN`。
4. 所有代理服务 rule-set 的 AAAA 请求返回空 `NOERROR`。这条规则位于
   Google 和国内 DNS 规则之前，避免 rule-set 交集返回代理业务 IPv6。
5. `geosite-google` 的非 AAAA 查询并发评估 Google DNS 和 Cloudflare DNS，
   两者都通过主代理组。
6. `geosite-cn` 和 `geosite-category-pt` 并发评估 Tencent DNS 和 AliDNS。
7. 未命中上述国内规则的 AAAA 请求返回空 `NOERROR`。
8. 其他查询并发评估 Google DNS 和 Cloudflare DNS。

这保证 Google 以及其他代理业务不会先命中国内 DNS 规则而获得 AAAA。
国内域名仍允许 A/AAAA；其他 AAAA 被空答复，路由层再拒绝应用内置 DoH、
缓存或硬编码地址带来的非国内公网 IPv6。

## DNS 并发评估语义

每组 DNS 竞速都生成两个 `evaluate` 和配套的 `respond`/`route` 规则：

- 最快返回的有效地址立即胜出。
- 两者都没有有效地址时，才接受任一 `NXDOMAIN`。
- 仍无可用响应时，优先复用第二台已返回的错误响应；第二台尚无响应时，
  最后向它执行一次普通 route。

`RouteProfileBuilderTests` 和 `DnsProfileBuilderTests` 会校验关键规则的实际索引。
修改服务定义或国内规则时，应同时更新实现、顺序测试和本文。

[返回 README](../README.md)
