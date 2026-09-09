# 生成配置约定

本文记录不属于运行时选项、但会稳定出现在生成 JSON 中的约定。

## 平台差异

每个平台都包含一个 TUN inbound 和一个仅监听 `127.0.0.1:8848` 的 mixed inbound。
TUN 固定启用 `auto_route`、`strict_route` 和 `dns_mode: hijack`，平台差异如下。
全平台仍生成仅监听 `127.0.0.1:8848` 的 `mixed-in`，供显式配置的 SOCKS/HTTP
客户端使用，但不生成 `set_system_proxy` 或 `platform.http_proxy`，因此不会由
sing-box 自动修改系统 HTTP 代理设置。

| 平台 | TUN stack | 其他差异 |
| --- | --- | --- |
| Android | `system` | `mtu: 1400`；不为代理出站写入 TCP keepalive |
| Linux | `system` | `auto_redirect: true`；生成 `bridge-out` L3 直连和 kernel-level `bypass`；代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s` |
| Windows | `mixed` | 生成 `bridge-out` L3 直连；代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s` |

Linux 和 Windows 额外生成 sing-box 1.14 `bridge` outbound。仅对首个 `sniff`
之前可安全预匹配的私网地址和 `223.5.5.5` 直连规则，保留原 `DIRECT` 作为 L4
回退，并在它前面增加带 `preferred_by: bridge-out` 门控的 L3 route。Linux
还会再优先生成无 `outbound` 的 `bypass` 动作，使 `auto_redirect` 流量在相同
条件下从内核层直接绕过 sing-box；该动作在其他上下文会被跳过。

依赖嗅探、域名或后续服务优先级才能确定的国内直连规则不会提前，仍使用原
`DIRECT` 路径。仅属于 `mixed-in` 的规则也不参与 L3/bypass 扩展，保留的
loopback mixed 入站与 DIRECT 分流范围保持不变。

`bridge` 需要系统权限；Windows 依赖 WinDivert，Linux 的 `bypass` 依赖已启用的
`auto_redirect`。Android 不生成这些字段。

在目标平台启用 Tailscale 时，`taildrop_directory` 始终按目标平台生成：Android 使用
SFA 工作目录下的 `Taildrop`，Windows 使用
`$USERPROFILE\Downloads\Taildrop`，Linux 使用 `$HOME/Downloads/Taildrop`。环境
变量由目标机器上的 sing-box 在运行时展开。

## DNS 与持久化缓存

- 生成配置包含官方 `$schema`。
- DNS 默认使用 `prefer_ipv4`，缓存容量为 `4096`，并启用超时为 `3d` 的
  optimistic 缓存和 reverse mapping。
- 代理节点域名固定通过 `node-resolver` 以 `ipv4_only` 解析；所有代理出站的
  `domain_resolver` 也显式指定 `ipv4_only` 并禁用 optimistic 过期缓存。
  代理出站的内部解析不会经过普通 DNS 规则，因此该约束直接写在每个出站上；
  IPv6 字面量代理节点会在生成时被校验器拒绝。
- 普通 DNS 查询命中代理节点域名时同样禁用 optimistic 过期缓存；Tailscale DNS
  查询也显式禁用它，避免地址变更后继续使用旧记录。
- `bootstrap` 仅在目标平台启用 Tailscale endpoint 时生成；它是 endpoint 的
  独立直连 DoH 启动解析器，不会在未启用 Tailscale 的配置中占位。
- 代理服务域名的 AAAA 查询返回空 `NOERROR`；国内 DNS 规则仍允许 A/AAAA。
- `experimental.cache_file` 使用 `cache.db`，并通过 `store_dns` 持久化 DNS 缓存。
- `cache_id` 是 YAML `proxies` 列表的规范化 SHA-256；字段顺序不影响身份。
  只要核心代理列表相同，不同平台或其他 Clash 配置项会复用同一缓存身份。

## 出站与 rule-set

- AnyTLS 的 `idle-session-timeout`、下划线别名以及旧
  `idle-timeout` 输入统一生成官方 `idle_session_timeout`；纯数字输入按秒转换。
- VLESS `packet-encoding`（兼容 `packet_encoding`）会按来源生成 `xudp`、
  `packetaddr` 或显式空值；未提供时保持既有 `xudp` 默认。
- Reality 转换要求有效的 32 字节 Base64URL 公钥和显式 short ID。short ID
  可以为空，否则必须是最多 8 字节的偶数位十六进制字符串；错误会在节点转换阶段
  直接报告，不再生成空字段。
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

## sing-box API

API 默认不生成。启用 `SingboxApi:Enabled` 后，顶层增加一个仅监听
`127.0.0.1:9090` 的 `api` service，并启用工作目录下的 `dashboard`。
Dashboard 下载复用 `rule-set-direct` HTTP client；允许的浏览器 origin 被限制为
同端口的 `127.0.0.1` 与 `localhost`，`access_control_allow_private_network` 保持
`false`。禁用时顶层 `services` 字段完全省略。

## 节点与分组

- 同一地区至少命中两个节点时才生成地区 selector。
- 主 selector 依次包含地区组、单个节点和直连；默认使用第一个地区组，
  没有地区组时使用第一个节点，再无节点时选择直连。
- AI、Google、Spotify 和 Microsoft 服务组在美国地区组存在时默认选择它；
  Steam 在香港地区组存在时默认选择它。否则回退为主代理组。

[返回 README](../README.md)
