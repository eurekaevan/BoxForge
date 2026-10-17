# 协议转换与验证边界

研究基线为 Mihomo Meta/Alpha `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`
及官方 sing-box `1.15.0-alpha.8`、revision
`b609f959f57ce34416c51c7b87ce4a76f2e1df56`。官方构建使用 grpc-lite，
不是带 `with_grpc` 的定制构建；更换构建后必须重新资格验证。

## TLS 与 transport 支持范围

TLS 指纹仅接受两端等价支持的现代命名 profile 和 `randomized`；`none` 关闭
uTLS。经典版本、历史 Chrome 别名、不同随机分布的 `random` 和未知值拒绝。
Reality 必须有有效 uTLS，VLESS 还必须显式启用 TLS；不擅自补默认指纹。
完整字段约束见[生成配置](generated-config.md#transport-与-shared-tls)。

| 来源能力 | 结论 | 原因 |
| --- | --- | --- |
| Trojan gRPC | 拒绝 | 来源 authority 为裸 SNI/server；目标 grpc-lite 自动增加端口，没有独立 authority 字段。没有找到普通域名的安全子集 |
| HTTP/http-opts | 拒绝 | 来源首包生成 Content-Length、后续发送原始流；目标 HTTP/1 或 TLS HTTP/2 的 body framing 不同 |
| H2/h2-opts | 拒绝 | h2c 不能映射到无 TLS 的目标 HTTP/1；TLS PUT 子集仍有每 stream 物理连接与目标连接复用差异，没有跨核心证明 |
| HTTPUpgrade | 保留受限子集 | 仅普通路径、标量 headers，无 fast-open、active early data 或 Reality；URL 特殊形式仍拒绝 |
| xHTTP | 拒绝 | 指定版本目标 transport union 无 xHTTP；不能用 HTTP/WS 近似模式、session 和 chunk 语义 |
| 静态 WS `Sec-WebSocket-Protocol` | 拒绝 | 锁定目标核心第一次握手后删除持久化 header，后续连接丢失；标准 early-data 独立字段不受影响 |

没有新增未经跨核心验证的 transport 支持；现有 source guard 与反例测试继续生效。
HTTPUpgrade/WS/gRPC 的 sing-box ↔ sing-box 回环仅证明生成配置可以互操作，
不能替代 Mihomo ↔ sing-box 的来源语义证明。

## 可重复的本地验证

从仓库根目录运行 .NET 验证：

```bash
dotnet format BoxForge.slnx --verify-no-changes
dotnet build BoxForge.slnx --no-incremental --warnaserror
dotnet test BoxForge.slnx --no-build
```

再使用 CLI 从 `Tests/BoxForge.Tests/Fixtures` 生成三平台配置，分别覆盖 API
关闭和开启。下面的 Bash 命令使用独立临时目录，不替换正式输出目录；
将 `singbox_core` 改为上述官方核心的绝对路径。

```bash
singbox_core=/absolute/path/to/sing-box
verification_root=$(mktemp -d /tmp/boxforge-verification.XXXXXX)

BOXFORGE_SingboxApi__Enabled=false \
dotnet run --no-build --project src/BoxForge.Cli -- generate \
  --input-dir Tests/BoxForge.Tests/Fixtures \
  --output-dir "$verification_root/standard" --platform all || exit

BOXFORGE_SingboxApi__Enabled=true \
dotnet run --no-build --project src/BoxForge.Cli -- generate \
  --input-dir Tests/BoxForge.Tests/Fixtures \
  --output-dir "$verification_root/api" --platform all || exit

for config in "$verification_root"/*/*/*/config.json; do
  "$singbox_core" check -c "$config" || exit
done

python3 scripts/verify-dns-races.py "$singbox_core" \
  "$verification_root/standard/profiles/Linux/config.json" || exit

node scripts/verify-transports.mjs "$singbox_core" \
  "$verification_root/standard/transport/Linux/config.json" || exit
node scripts/verify-shadowsocks.mjs "$singbox_core" \
  "$verification_root/standard/shadowsocks/Linux/config.json" || exit
node scripts/verify-session-protocols.mjs "$singbox_core" \
  "$verification_root/standard/hysteria2/Linux/config.json" hysteria2 || exit
node scripts/verify-session-protocols.mjs "$singbox_core" \
  "$verification_root/standard/anytls/Linux/config.json" anytls || exit
```

DNS 验证脚本读取真实生成的桌面 DNS 规则，将上游替换为本地受控 DoH，
将分类替换为同标签 inline 测试域名；不创建 TUN、不使用真实订阅或公网 DNS。
需要 Python 3 和 OpenSSL，本地 DoH 通过显式信任证书使用 HTTP/1.1，不能据此
证明生产 DoH 的 HTTP/2、代理链路或 Gmail 实际性能。它覆盖地址/空答竞争、
CNAME、附加区与 IPv6 地址、非 A 查询、分类隔离、TTL/过期缓存和未变的 5 秒
负答等待。诊断和结果写入独立的 `/tmp/boxforge-dns-races.*` 目录。

协议验证脚本使用真实生成的 Linux leaf，而不是重新手写客户端字段。对应范围：

- `scripts/verify-transports.mjs`：既有 VLESS WS/gRPC/HTTPUpgrade、Trojan WS/HTTPUpgrade。
- `scripts/verify-shadowsocks.mjs`：普通 SS TCP 与 UoT v1/v2 UDP echo；SS 服务端仅监听 TCP，防止原生 UDP 假通过。
- `scripts/verify-session-protocols.mjs`：最后一个参数为 `hysteria2` 或 `anytls`；
  验证 HY2 默认、显式带宽、Gecko、保留 20 秒间隔的跳端口，以及 AnyTLS 会话字段。

脚本只改回环拨号地址/端口和临时证书信任，保留生成的协议字段；临时服务与进程
由脚本清理。HY2 跳端口使用两个临时 UDP 转发端口，观察保留间隔后的 UDP source
socket 轮换并报告目的端口流量；[指定依赖随机选端口](https://github.com/SagerNet/sing-quic/blob/4f371c86a365/hysteria/hop.go)，合法跳转可能选回原目的端口，
因此不要求一次跳转后两个目的端口都被选中。
需要 Node.js、OpenSSL；transport/session 脚本还需要 curl。
脚本核对版本及 revision；fixture 全部使用虚构凭据。

SS obfs HTTP/TLS 与 v2ray-plugin 目前只有 serializer/官方核心配置检查，
目标 SS inbound 不提供插件服务端，因此脚本明确输出 `CHECK-ONLY`，不是 runtime PASS。
这些验证不包含生产订阅、远程服务器、真实 TUN、防火墙、Android 设备或网络泄漏抓包。

## 配置默认值与验证覆盖

输出层省略与锁定核心有效默认值相同的字段，内部来源映射模型不变。
回环脚本在改写拨号地址前，将省略的默认 SNI 物化为原 server，避免证书名称随
测试地址变化；UoT 和会话字段按官方有效默认值判断，不重新注入被省略的设置。

常规验收包括 `dotnet format`、warnings-as-errors build、全部测试、
5 个 fixture × 3 平台 × API 开关两种状态的 30 份 JSON 官方检查。
另外核对地区 AUTO 层级、有效默认选择、DNS/route 引用及控制面 resolver。
单元测试覆盖循环和超限 YAML、指纹/Reality 拒绝、静态 WS subprotocol 拒绝与标准
early-data 保留；真实 Kestrel 子进程覆盖环境开关和截断 multipart 的错误契约。

| 场景 | 回环脚本验证内容 |
| --- | --- |
| SS plain TCP、UoT v1/v2 | HTTP/UDP echo，UoT 后端仅接受 TCP |
| SS obfs HTTP/TLS、v2ray-plugin WS/WS-TLS | CHECK-ONLY：无插件服务端，不标为 runtime PASS |
| HY2 默认、100/80 Mbps、Gecko 600/1200、固定 20 秒 hopping | HTTP body 与 hopping 后 UDP source socket 轮换 |
| AnyTLS session fields | 生成字段断言与 HTTP body |
| VLESS WS/gRPC、Trojan WS、VLESS/Trojan HTTPUpgrade | HTTP body；HTTPUpgrade 仍有下述历史风险 |

## 已知验证边界

协议脚本仅验证 sing-box 与 sing-box 回环，配置检查不等于实时 DNS、tailnet、
kernel bypass、Windows ZIP 解压、真实 TUN 或设备级 IPv6 防逃逸证明。

历史复跑曾在 VLESS HTTPUpgrade 上出现
一次 `unknown protobuf message header: 137` / 空 HTTP 响应；随后独立完整重跑
五种场景全部通过。成因尚未证实，因此 HTTPUpgrade 不能标为稳定运行资格已证明；
未添加自动重试，也未改写正式 transport 输出来掩盖该失败。

其他未经证明的路径包括 Reality、全部 uTLS 指纹和真实远程部署。
每次验收报告应注明实际运行的测试、产物和核心版本；本文定义验证方法与边界，
不把历史通过记录视为当前代码或目标设备已经通过。

[返回 README](../README.md)
