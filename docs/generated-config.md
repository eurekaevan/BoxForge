# 生成配置约定

本文记录不属于运行时选项、但会稳定出现在生成 JSON 中的约定。

## 平台差异

每个平台都包含一个 TUN inbound 和一个仅监听 `127.0.0.1:8848` 的 mixed inbound。
TUN 固定启用 `auto_route`、`strict_route` 和 `dns_mode: hijack`，平台差异如下：
全平台同时启用 `platform.http_proxy`，并将系统 HTTP 代理指向同一配置中的
`127.0.0.1:8848` mixed inbound，形成 TUN + system HTTP proxy 模型。

| 平台 | TUN stack | 其他差异 |
| --- | --- | --- |
| Android | `system` | `mtu: 1400`；不为代理出站写入 TCP keepalive |
| Linux | `system` | `auto_redirect: true`；代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s` |
| Windows | `mixed` | 代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s` |

在目标平台启用 Tailscale 时，`taildrop_directory` 始终按目标平台生成：Android 使用
SFA 工作目录下的 `Taildrop`，Windows 使用
`$USERPROFILE\Downloads\Taildrop`，Linux 使用 `$HOME/Downloads/Taildrop`。环境
变量由目标机器上的 sing-box 在运行时展开。

## DNS 与持久化缓存

- 生成配置包含官方 `$schema`。
- DNS 默认使用 `prefer_ipv4`，缓存容量为 `4096`，并启用超时为 `3d` 的
  optimistic 缓存和 reverse mapping。
- 代理节点域名固定通过 `node-resolver` 以 `ipv4_only` 解析；所有代理出站的
  `domain_resolver` 也显式指定 `ipv4_only`。IPv6 字面量代理节点会在生成时
  被校验器拒绝。
- 代理节点域名与 Tailscale DNS 显式禁用 optimistic 过期缓存，避免地址
  变更后继续使用旧记录。
- 代理服务域名的 AAAA 查询返回空 `NOERROR`；国内 DNS 规则仍允许 A/AAAA。
- `experimental.cache_file` 使用 `cache.db`，并通过 `store_dns` 持久化 DNS 缓存。
- `cache_id` 是 YAML `proxies` 列表的规范化 SHA-256；字段顺序不影响身份。
  只要核心代理列表相同，不同平台或其他 Clash 配置项会复用同一缓存身份。

## 出站与 rule-set

- Hysteria2 出站使用 `hop_interval: 30s`、`hop_interval_max: 60s` 和
  `bbr_profile: standard`。
- 远程 rule-set 每天更新，通过默认 HTTP client `rule-set-direct` 直接拨号下载；
  该 HTTP client 使用本地 DNS 的 `ipv4_only` 解析，不经 `DIRECT` outbound
  二次解析。
- 广告过滤同时使用 anti-AD 的 `anti-ad-sing-box.srs` 和 SagerNet 的
  `geosite-category-ads-all.srs`。
- mixed inbound 的代理业务域名和最终代理回退域名在路由前执行
  `resolve` + `ipv4_only`。公网 IPv6 只有命中 `geoip-cn` 时才进入 `DIRECT`；
  其他公网 IPv6 在所有代理业务路由之前被拒绝。私网和 Tailscale 路径不受
  这条公网限制影响。

## 节点与分组

- 同一地区至少命中两个节点时才生成地区 selector。
- 主 selector 依次包含地区组、单个节点和直连；默认使用第一个地区组，
  没有地区组时使用第一个节点，再无节点时选择直连。
- AI、Google、Spotify 和 Microsoft 服务组在美国地区组存在时默认选择它；
  Steam 在香港地区组存在时默认选择它。否则回退为主代理组。

[返回 README](../README.md)
