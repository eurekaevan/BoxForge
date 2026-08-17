# DNS 与路由优先级

sing-box 规则顺序会直接改变行为，因此 BoxForge 将生成顺序视为可测试的公开约定。
本文记录当前构建器实际输出的顺序，便于修改规则时评估优先级影响。

## 路由规则顺序

`RouteProfileBuilder` 按以下顺序生成顶层规则：

1. 劫持 TUN 和 mixed inbound 的 DNS 流量。
2. 启用 Tailscale 时，先路由 Tailscale endpoint 声明为首选的目标。
3. 直连私网地址和本地 DNS bootstrap 地址。
4. 拒绝固定 STUN UDP 端口，然后分别嗅探 TCP HTTP/TLS 与 UDP QUIC。
5. 拒绝 anti-AD 和 `geosite-category-ads-all`。
6. 按服务定义顺序拒绝 AI、Google 的 UDP/443，促使 QUIC 回退 TCP。
7. 将 AI 路由到 `AI`，再将 Google 路由到 `Google`。
8. 直连命中国内 rule-set 的 IPv6，然后拒绝其他公网 IPv6。
9. 放行国内域名的 UDP/443；mixed inbound 先解析目标后再放行 `geoip-cn`，
   其他 UDP/443 全部拒绝。
10. 生成其他服务分流，当前为 Spotify、Steam 和 Microsoft。
11. 直连 `geosite-cn`/`geosite-category-pt`；mixed inbound 解析后再直连 `geoip-cn`。
12. 未命中规则的流量使用主代理组。

对业务分流而言，核心优先级是：

```text
广告拒绝 → AI → Google → 国内直连 → 最终代理
```

AI 和 Google 必须位于所有引用 `geosite-cn` 的国内规则之前，因为
`geosite-cn` 可能同时包含 Google 相关域名。

## DNS 规则顺序

`DnsProfileBuilder` 的顶层顺序是：

1. 启用 Tailscale 时，将 MagicDNS 和分流后缀交给 Tailscale DNS，并禁用
   optimistic 过期缓存。
2. 代理节点域名使用专用本地解析器，仅请求 A 记录，并禁用 optimistic 缓存。
3. 广告域名直接返回 `NXDOMAIN`。
4. `geosite-google` 先并发评估 Google DNS 和 Cloudflare DNS，两者都通过主代理组。
5. `geosite-cn` 和 `geosite-category-pt` 并发评估 Tencent DNS 和 AliDNS。
6. 未命中上述国内规则的 AAAA 请求返回空 `NOERROR`。
7. 其他查询并发评估 Google DNS 和 Cloudflare DNS。

这保证 Google 域名优先使用远程 DNS，不会先命中国内 DNS 规则。国内域名允许
A/AAAA；其他 AAAA 被空答复，避免非国内公网 IPv6 绕过后续代理策略。

## DNS 并发评估语义

每组 DNS 竞速都生成两个 `evaluate` 和配套的 `respond`/`route` 规则：

- 最快返回的有效地址立即胜出。
- 两者都没有有效地址时，才接受任一 `NXDOMAIN`。
- 仍无可用响应时，优先复用第二台已返回的错误响应；第二台尚无响应时，
  最后向它执行一次普通 route。

`RouteProfileBuilderTests` 和 `DnsProfileBuilderTests` 会校验关键规则的实际索引。
修改服务定义或国内规则时，应同时更新实现、顺序测试和本文。

[返回 README](../README.md)
