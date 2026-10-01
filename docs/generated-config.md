# 生成配置约定

本文记录不属于运行时选项、但会稳定出现在生成 JSON 中的约定。

## 平台差异

每个平台都包含一个 TUN inbound 和一个仅监听 `127.0.0.1:8848` 的 mixed inbound。
TUN 固定启用 `auto_route` 和 `dns_mode: hijack`。`strict_route` 仅在 Linux/
Windows 显式启用；指定版本 Android feature matrix 标记该能力未实现，因此省略。
平台差异如下。
全平台仍生成仅监听 `127.0.0.1:8848` 的 `mixed-in`，供显式配置的 SOCKS/HTTP
客户端使用，但不生成 `set_system_proxy` 或 `platform.http_proxy`，因此不会由
sing-box 自动修改系统 HTTP 代理设置。

| 平台 | 差异 |
| --- | --- |
| Android | 不为代理出站写入 TCP keepalive |
| Linux | `auto_redirect: true`；生成 `bridge-out` L3 直连和 kernel-level `bypass`；代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s` |
| Windows | 生成 `bridge-out` L3 直连；代理出站使用 `tcp_keep_alive: 1m` 和 `tcp_keep_alive_interval: 30s` |

三个平台均不生成已弃用的 `stack`，由 sing-box 1.15 使用 sing-tun 自有 TCP/IP
栈；也不生成 `mtu`，由 sing-box 按目标平台和运行环境采用默认值。

Linux 和 Windows 额外生成 sing-box 1.15 `bridge` outbound，并按规则可判定的
阶段生成两类 L3 forwarding 层：

- 私网地址和 `223.5.5.5` 在首个 `sniff` 之前预匹配；原 `DIRECT` 前增加带
  `preferred_by: bridge-out` 门控的 L3 route。
- 国内 UDP/443、`cn` 和 `cnip`
  在 UDP sniff 之后、各自原始规则所在位置增加仅匹配 `tun-in` + UDP 的 L3
  route。它们不会被搬到 sniff 之前，因此仍保留原有服务优先级和域名嗅探语义。

TUN 仍配置 `fd00::1/126`，但它只用于让操作系统把 IPv6 流量送入 sing-box，
不是 IPv6 连通能力。DNS 劫持之后立即生成全局 `ip_version: 6` 拒绝，在任何
DIRECT、Tailscale、bridge、bypass 或 sniff 之前终止 IPv6。解析阶段之后再生成
同样的兜底拒绝，覆盖 mixed/domain 等无法在 Pre-match 阶段判定的路径。

Linux 在每条上述 L3 route 之前再生成无 `outbound` 的 `bypass` 动作，使
`auto_redirect` 流量在相同条件下从内核层直接绕过 sing-box；该动作在其他
上下文会被跳过。每条原 `DIRECT` 都原位保留作为 correctness fallback，覆盖
TCP 和 UDP 无法完成 sniff 等不能使用 L3 forwarding 的情况。仅属于 `mixed-in`
的规则不参与 L3/bypass 扩展，loopback mixed 入站与 DIRECT 分流范围保持不变。

`bridge` 需要系统权限；Windows 依赖 WinDivert，Linux 的 `bypass` 依赖已启用的
`auto_redirect`。Android 不生成这些字段。

三个平台默认生成 Tailscale endpoint，并启用 `on_demand: true`。启用时，
`taildrop_directory` 始终按目标平台生成：Android 使用
SFA 工作目录下的 `Taildrop`，Windows 使用
`$USERPROFILE\Downloads\Taildrop`，Linux 使用 `$HOME/Downloads/Taildrop`。环境
变量由目标机器上的 sing-box 在运行时展开。

## DNS 与持久化缓存

- 生成配置包含官方 `$schema`。
- DNS 查询默认超时为 `5s`。Android 正常只查询首选 resolver，首选查询使用
  `2s` 超时，失败后才查询备用；Linux/Windows 同时查询两家，并让最快的
  `NOERROR` 胜出。NXDOMAIN 不参与竞速，双方没有 `NOERROR` 时仍首选 primary
  NXDOMAIN。两家都失败时明确返回 `SERVFAIL`；详见
  [DNS 分类与回退](routing-and-dns.md)。
- DNS 默认使用 `ipv4_only`，缓存容量为 `4096`，并启用超时为 `6h` 的
  optimistic 缓存和 reverse mapping。
- 代理节点、Tailscale endpoint 和直连出站继承
  `route.default_domain_resolver`：直连 AliDNS、`ipv4_only`、禁用 optimistic
  过期缓存。它们的域名解析不进入 client DNS 分类规则。IPv6 字面量代理节点
  会在生成时被校验器拒绝。
- rule-set 和 Dashboard HTTP client 显式使用同一 AliDNS 解析设置；
  Tailscale split DNS 也禁用 optimistic 过期缓存。
- 所有 AAAA 查询在规则链首部返回空 `NOERROR`，国内 DNS 也只提供 IPv4。
- `experimental.cache_file` 使用 `cache.db`，并通过 `store_dns` 持久化 DNS 缓存。
- `cache_id` 是 YAML `proxies` 列表的规范化 SHA-256；字段顺序不影响身份。
  只要核心代理列表相同，不同平台或其他 Clash 配置项会复用同一缓存身份。

## 出站与 rule-set

- Clash YAML 中的重复键会在解析阶段拒绝，不会以“后值覆盖前值”
  的方式静默改变节点字段。
- 每种已支持节点协议先检查自己声明的 source schema。未支持或无法分类的
  显式连接字段在 strict 生成中报错；non-strict 转换会跳过整个节点并告警。
  已知的 UI 元数据和确认无连接作用的值会被明确列为忽略项。
- 出站模型不携带 BoxForge 调优默认值。正式生成时仅对缺失字段填入
  `connect_timeout: 5s`、Linux/Windows TCP keepalive `1m`/`30s`，
  以及未指定跳端口间隔的 HY2 `hop_interval: 30s`、`hop_interval_max: 60s`。
  没有正显式带宽时才补 `bbr_profile: standard`；显式固定间隔不补随机上限。
  已有显式出站值不会被调优覆盖；Android 不注入
  桌面 keepalive。
- AnyTLS 的 `idle-session-timeout`、下划线别名以及旧
  `idle-timeout` 输入统一生成官方 `idle_session_timeout`；纯数字输入按秒转换。
  `idle-session-check-interval` 使用同一正 duration 校验；`min-idle-session`
  映射非负整数，`client-metadata` 保留字符串并检查 settings frame 安全边界。
  所有新增字段未提供时省略，不主动注入会话维护值。
- VLESS `packet-encoding`（兼容 `packet_encoding`）会按来源生成 `xudp`、
  `packetaddr` 或显式空值；未提供时保持既有 `xudp` 默认。
- Reality 转换要求有效的 32 字节 Base64URL 公钥和显式 short ID。short ID
  可以为空，否则必须是最多 8 字节的偶数位十六进制字符串；错误会在节点转换阶段
  直接报告，不再生成空字段。
- Hysteria2 显式 `up`/`down` 转为精确整数 Mbps；无法精确转换时拒绝。
  显式 `hop-interval` 为整数秒或范围，固定间隔不注入随机上限；缺失时保留
  BoxForge 的 `30s/60s` 调优，没有正显式带宽才补 `standard`。Gecko 保留包大小和密码，
  QUIC/Realm 的未支持扩展仍拒绝，不会静默降级。
- 未支持的 transport/TLS 组合（见下节）、AnyTLS disable-reuse
  以及不能精确传递的 Shadowsocks 插件配置
  当前均 fail-fast；详见[来源语义与调优优先级](architecture.md#来源语义与调优优先级)。
- 远程 rule-set 每天更新，通过默认 HTTP client `http-ruleset-proxy` 走代理下载。
  有地区 AUTO 时选择首个地区 AUTO，否则选择首个真实节点；即使主组被手动切到
  `DIRECT`，rule-set 下载也不会随之改走直连。下载域名仍由
  `dns-direct-alidns` 以
  `ipv4_only` 解析，以免代理 DNS 成为冷启动依赖。
- 规则集来源与内部 tag 的映射如下；代码中的 DNS/route 引用只使用内部 tag，
  不依赖上游文件名。八个 tag 均唯一声明并按 `1d` 更新。

  | 来源 | 内部 tag → 文件名 | URL 基础路径 |
  | --- | --- | --- |
  | DustinWin release | `ads→ads.srs`、`ai→ai.srs`、`spotify→spotify.srs`、`games→games.srs`、`cn→cn.srs`、`cnip→cnip.srs` | `https://github.com/DustinWin/ruleset_geodata/releases/download/sing-box-ruleset/` |
  | MetaCubeX `sing` 分支 | `google→google.srs`、`microsoft→microsoft.srs` | `https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/sing/geo/geosite/` |

  DustinWin 同名文件共用 `{tag}.srs` 模板；MetaCubeX 的文件逐项声明。
  广告过滤使用 `ads`；Games 使用 `games`，覆盖范围由上游规则维护，
  不再限于单一游戏平台。DustinWin 的 `ads` 来源于 anti-AD，`games` 明确排除
  `games-cn`；两者的实际命中范围与迁移前的单项分类不同。
- mixed inbound 的代理业务域名、国内域名和最终代理回退域名在路由前执行
  `resolve` + `ipv4_only`。全部 IPv6 使用原生 `ip_version: 6` 匹配，并在
  DIRECT、Tailscale 和所有代理业务路由之前被拒绝。
- 未被前置 Tailscale、私网或 bootstrap 直连规则处理的 UDP 流量会同时嗅探
  QUIC 和 STUN，并拒绝识别出的 STUN 协议；不再根据 3478、3479、19302 或
  19303 等固定端口拒绝普通 UDP 流量。
- AI、Google 和最终兜底的 UDP/443 拒绝规则写入 `no_drop: true`，持续返回拒绝
  响应以促使 QUIC 回退 TCP；STUN、广告和两道 IPv6 拒绝不启用该字段。

## Transport 与 shared TLS

VLESS 支持省略/`tcp`、`ws`、受限基础 `grpc`；Trojan 支持省略/`tcp` 和
`ws`。两者可通过 WS 选项启用无 fast-open 的 HTTPUpgrade。未知 network
不会降级为 TCP，不注入自定义 path、service-name 或 ALPN。

| 来源字段 | sing-box 字段 | 条件 |
| --- | --- | --- |
| `ws-opts.path` | `transport.path` | 普通路径；缺失时省略 |
| `ws-opts.headers` | `transport.headers` | 字符串值，不裁剪或合并 |
| `ws-opts.max-early-data` | `transport.max_early_data` | 0 到 2147483647 的整数；缺失时省略 |
| `ws-opts.early-data-header-name` | `transport.early_data_header_name` | 空/缺失时沿用路径携带 early-data 的行为 |
| `ws-opts.v2ray-http-upgrade: true` | `transport.type: httpupgrade` | HTTP 101 后使用原始字节流，而非 WS framing |
| HTTPUpgrade 的 `headers.Host` | `transport.host` | 其他 headers 保留，Host 不重复生成 |
| `grpc-opts.grpc-service-name` | `transport.service_name` | 仅 VLESS，非空普通服务名 |
| `alpn` | `tls.alpn` | 保留顺序、重复项和值；缺失时省略 |

`ws_opts`/`grpc_opts` 及已支持的连字符子字段兼容下划线别名。双别名同时出现
仍会失败。header 名称不是配置别名，不做连字符转换。嵌套未知字段会报告完整
路径，例如 `ws-opts.future-field`；无法转换时 non-strict 也只会跳过整个节点。

HTTP Host 按来源实际行为生成：无显式 Host 时，VLESS TLS 使用 server，
Trojan 使用 SNI/server。VLESS WS 的 TLS SNI 在未指定 `sni`/`servername`
时使用 headers.Host。这是来源默认行为的映射，不是 BoxForge tuning。
无 TLS 的 VLESS WS 必须提供 Host，否则 Mihomo 随机 Host 无法等价表达。

继续拒绝的 transport 输入：

- URL authority/scheme/query/fragment/percent-escape 路径，包括 `?ed=`；两端
  URL 解析不同，本轮仅支持普通路径与显式 early-data 字段。
- 列表/对象 header、大小写重复或无效 header、握手保留 header；active
  early-data 不得覆盖 Host 或握手保留字段。
- `v2ray-http-upgrade-fast-open: true`；HTTPUpgrade 与非零 early-data 或
  非空 early-data-header-name 的组合，目标没有等价字段。
- 缺失/空 gRPC service-name、自定义完整 RPC path；`grpc-user-agent`、
  `ping-interval`、`max-connections`、`min-streams`、`max-streams`。
- VLESS gRPC 显式 `sni`/`servername`：指定官方核心使用 grpc-lite，将 TLS
  server_name 拼成 `host:port` authority，而 Mihomo 对显式名称不加端口。
  目标没有独立 authority 字段，只接受来源默认 `server:port` authority。
- **Trojan gRPC**：Mihomo 默认使用裸 SNI/server authority，grpc-lite 会增加端口。
  经确认，本阶段保留拒绝，不为支持率接受这个差异。
- Reality + WS/HTTPUpgrade：Mihomo WS 分支不应用 Reality，目标会应用。
  此组合经确认拒绝；Reality 仍可用于现有 TCP 和满足限制的 VLESS gRPC。
- VLESS WS 或带 uTLS 的 Trojan WS/HTTPUpgrade 显式 ALPN 不等于
  `[http/1.1]`；VLESS gRPC 显式 ALPN 不等于 `[h2]`；transport 下显式空 ALPN。
  它们无法同时保留来源的强制/空值行为和目标的显式列表行为。
- HTTP/http-opts：Mihomo 首包 Content-Length HTTP 伪装与目标的原始/流式载荷
  不同，单 path 也不能证明等价。H2/h2-opts 的非 TLS 模式在来源中使用 h2c，
  目标无 TLS 的 HTTP transport 却使用 HTTP/1；TLS 子集虽有相似 PUT framing，
  来源逐 stream 建立/关闭物理连接，目标复用 HTTP/2 连接，生命周期差异未获
  精确映射与跨核心验证，因此继续拒绝。xHTTP 没有对应目标 transport，
  不近似转换为 HTTP/WS。

TLS 仍拒绝 `fingerprint`、`name-cert-verify`、`certificate`/`private-key`，以及
ShadowTLS、Restls、JLS、tlsmirror、ECH；非空 VLESS encryption 和 Trojan
ss-opts 也不支持。name-cert-verify 不能映射为改变 SNI 的 server_name；mTLS
的路径/inline PEM、链与密钥处理未完成可靠转换。

证书指纹的两端 hash 都可使用整张 DER 的 SHA-256，叶证书 pin 都替代普通
CA/名称验证，而非叠加校验。但 Mihomo 还能匹配中间/根证书，并在该分支验证
证书链和名称；目标 `certificate_sha256` 只匹配叶证书。仅凭源 hash 无法判断
层级，所以本轮不生成该字段，也不进行未经证明的 hex→base64 转换。

### 源码依据与验证边界

研究基线：Mihomo Meta `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`，
sing-box `1.15.0-alpha.8` / `b609f959f57ce34416c51c7b87ce4a76f2e1df56`。
官方 [Mihomo transport](https://wiki.metacubex.one/en/config/proxies/transport/)、
[TLS](https://wiki.metacubex.one/en/config/proxies/tls/) 文档与固定源码交叉核对：

- Mihomo [VLESS](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/vless.go)、
  [Trojan](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/adapter/outbound/trojan.go)、
  [WS](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/transport/vmess/websocket.go)、
  [gRPC](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/transport/gun/gun.go)、
  [证书链 pin](https://github.com/MetaCubeX/mihomo/blob/88dcbf7f1614a67c3b36b848ee3592dfa92ada36/component/ca/fingerprint.go)。
- sing-box [transport model](https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/option/v2ray_transport.go)、
  [WS](https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/transport/v2raywebsocket/client.go)、
  [HTTPUpgrade](https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/transport/v2rayhttpupgrade/client.go)、
  [grpc-lite](https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/transport/v2raygrpclite/client.go)、
  [leaf pin](https://github.com/SagerNet/sing-box/blob/b609f959f57ce34416c51c7b87ce4a76f2e1df56/common/tls/std_client.go)。

带 `with_grpc` 的非官方构建具有不同 authority 行为，不属于本轮验证范围。
`Tests/BoxForge.Tests/Fixtures/transport.yaml` 使用虚构凭据，经过正式 YAML →
engine → Android/Linux/Windows JSON 路径；测试断言实际字段值、嵌套拒绝、
别名冲突、ALPN 与 uTLS/Reality 组合及 source-wins。原 tuning/DNS/route 不变。

```bash
dotnet run --project src/BoxForge.Cli -- generate \
  --input-dir Tests/BoxForge.Tests/Fixtures \
  --output-dir /tmp/boxforge-transport-output --platform all
# 分别对 transport/{Android,Linux,Windows}/config.json 使用指定官方核心 check。
node scripts/verify-transports.mjs /absolute/path/to/sing-box \
  /tmp/boxforge-transport-output/transport/Linux/config.json
```

loopback 脚本检查版本/revision，提取真实生成节点，仅替换 loopback 地址/端口并
信任临时证书，保留 transport、ALPN、SNI 和 tuning。它启动临时 sing-box
server/client，验证 VLESS WS/gRPC/HTTPUpgrade 和 Trojan WS/HTTPUpgrade
共五条 HTTP 响应链路，结束后清理进程和临时凭据。依赖 Node.js、OpenSSL、curl
和指定 sing-box CLI；不启用 TUN、不访问外网。它不是 Mihomo↔sing-box 跨核心
验证，也不能证明 Reality、所有 uTLS 指纹或真实服务器部署均兼容。

## sing-box API

API 默认不生成。启用 `SingboxApi:Enabled` 后，顶层增加一个仅监听
`127.0.0.1:9090` 的 `api` service，并启用工作目录下的 `dashboard`。
Dashboard 下载使用独立的 `http-dashboard-direct` 直连 HTTP client；允许的浏览器
origin 被限制为同端口的 `127.0.0.1` 与 `localhost`，
`access_control_allow_private_network` 保持
`false`。禁用时顶层 `services` 字段完全省略。

## 节点与分组

- 同一地区至少命中两个节点时，同时生成地区 selector 和对应的地区 AUTO：
  `🇺🇸 US AUTO`、`🇯🇵 JP AUTO`、`🇭🇰 HK AUTO`、`🇸🇬 SG AUTO`。
  地区 AUTO 只测试该地区真实节点；地区 selector 保留逐节点人工选择，并默认
  选择自己的 AUTO。
- URLTest 的 `url`、`interval`、`tolerance`、`idle_timeout` 和
  `interrupt_exist_connections` 均省略，使用 sing-box 官方默认值。现有 selector
  继续生成 `interrupt_exist_connections: true`。
- 主 `🚀 PROXIES` selector 依次保留地区组、单个节点和 `DIRECT` 的人工
  选择能力，不生成跨地区的全局 URLTest。美国地区组可用时默认选择 `🇺🇸 US`；
  否则默认第一个已生成的地区组。没有地区组时，有节点则选择第一个节点，
  没有节点则选择 `DIRECT`。
- AI、Google、Spotify 和 Microsoft 服务组在美国地区组存在时默认选择它；
  Games 在香港地区组存在时默认选择它。偏好地区仅有一个 leaf 时直接选择该节点，
  零个 leaf 才回退主代理组；偏好定义仅来自 `ProfileDefinitions.Services`。
  Service selector 不直接引用地区 AUTO，而由地区 selector 默认到 AUTO。
  主组仍保持上述“已生成地区组优先”的默认规则：单个 US 与多个 JP 并存时，
  主组默认 JP，偏好 US 的服务默认 US leaf；不新增单个 US 改写主组的策略。
- 真实代理节点保留订阅名称，但不得与 BoxForge 固定分组、内部基础设施、DNS
  响应或 rule-set tag 冲突；冲突会在节点转换阶段直接报错，不自动改名。

[返回 README](../README.md)
