# Stage C 协议资格与验证边界

研究基线为 Mihomo Meta/Alpha `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`
及官方 sing-box `1.15.0-alpha.8`、revision
`b609f959f57ce34416c51c7b87ce4a76f2e1df56`。官方构建使用 grpc-lite，
不是带 `with_grpc` 的定制构建；更换构建后必须重新资格验证。

## C4 transport 研究结论

| 来源能力 | 结论 | 原因 |
| --- | --- | --- |
| Trojan gRPC | 继续拒绝 | 来源 authority 为裸 SNI/server；目标 grpc-lite 自动增加端口，没有独立 authority 字段。没有找到普通域名的安全子集 |
| HTTP/http-opts | 继续拒绝 | 来源首包生成 Content-Length、后续发送原始流；目标 HTTP/1 或 TLS HTTP/2 的 body framing 不同 |
| H2/h2-opts | 继续拒绝 | h2c 不能映射到无 TLS 的目标 HTTP/1；TLS PUT 子集仍有每 stream 物理连接与目标连接复用差异，没有跨核心证明 |
| HTTPUpgrade | 保留 Stage B 的窄子集 | 仅普通路径、标量 headers，无 fast-open、active early data 或 Reality；URL 特殊形式仍拒绝 |
| xHTTP | 继续拒绝 | 指定版本目标 transport union 无 xHTTP；不能用 HTTP/WS 近似模式、session 和 chunk 语义 |

没有新增未经跨核心验证的 transport 支持；现有 source guard 与反例测试继续生效。
HTTPUpgrade/WS/gRPC 的 sing-box ↔ sing-box 回环仅证明生成配置可以互操作，
不能替代 Mihomo ↔ sing-box 的来源语义证明。

## 可重复的本地验证

先使用 CLI 从 `Tests/BoxForge.Tests/Fixtures` 生成三平台配置，再把官方核心路径
及对应 Linux `config.json` 传入脚本：

- `scripts/verify-transports.mjs`：既有 VLESS WS/gRPC/HTTPUpgrade、Trojan WS/HTTPUpgrade。
- `scripts/verify-shadowsocks.mjs`：普通 SS TCP 与 UoT v1/v2 UDP echo；SS 服务端仅监听 TCP，防止原生 UDP 假通过。
- `scripts/verify-session-protocols.mjs`：最后一个参数为 `hysteria2` 或 `anytls`；
  验证 HY2 默认、显式带宽、Gecko、保留 20 秒间隔的跳端口，以及 AnyTLS 会话字段。

脚本只改回环拨号地址/端口和临时证书信任，保留生成的协议字段；临时服务与进程
由脚本清理。HY2 跳端口使用两个临时 UDP 转发端口，观察保留间隔后的 UDP source
socket 轮换并报告目的端口流量；[指定依赖随机选端口](https://github.com/SagerNet/sing-quic/blob/4f371c86a365/hysteria/hop.go)，合法跳转可能选回原目的端口，
因此不要求一次跳转后两个目的端口都被选中。
需要 Node.js、OpenSSL；transport/session 脚本还需要 curl。

SS obfs HTTP/TLS 与 v2ray-plugin 目前只有 serializer/官方核心配置检查，
目标 SS inbound 不提供插件服务端，因此脚本明确输出 `CHECK-ONLY`，不是 runtime PASS。
这些验证不包含生产订阅、远程服务器、真实 TUN、防火墙、Android 设备或网络泄漏抓包。

## 本次验收与已知风险

| 阶段 | 实际结果 |
| --- | --- |
| C1 | UoT 显式版本对象；obfs 专用映射；v2ray-plugin websocket/mux=false、可选标准 TLS 的受限映射；未知插件/选项拒绝 |
| C2 | 精确整数 Mbps、固定/范围跳端口、BBR、Gecko；network/QUIC/Realm/uTLS 不等价部分仍拒绝 |
| C3 | 共用正 duration parser、check interval、非负 min idle、字符串 metadata 和 frame 安全校验 |
| C4 | 研究后维持 Trojan gRPC、HTTP/H2/xHTTP 拒绝；没有为支持率引入近似映射 |
| C5 | 地区 leaf inventory 与分组分离、单 preferred leaf 服务默认值、实际请求 policy-aware validator、Android strict_route 省略 |

最终 `dotnet format` 及 verify-no-changes、零警告 build、`dotnet test` **401/401**、
`git diff --check` 均通过。五个 fixture × 三个平台共 15 份原始配置通过官方
alpha.8 核心检查，版本与 revision 均核对。与 C3 输出比较，既有四个 fixture
的 12 份 DNS/route 内容和规则顺序完全一致；没有引入其他 DNS/routing 优化。

| 场景 | 本地运行证据 |
| --- | --- |
| SS plain TCP、UoT v1/v2 | PASS：HTTP/UDP echo，UoT 后端仅接受 TCP |
| SS obfs HTTP/TLS、v2ray-plugin WS/WS-TLS | CHECK-ONLY：官方核心接受配置，未建立插件服务端 |
| HY2 默认、100/80 Mbps、Gecko 600/1200、固定 20 秒 hopping | PASS：HTTP body 与 hopping 后 UDP source socket 轮换 |
| AnyTLS session fields | PASS：生成字段断言与 HTTP body |
| VLESS WS/gRPC、Trojan WS | PASS：既有完整回环复跑 |
| VLESS/Trojan HTTPUpgrade | 完整重跑 PASS，但首次复跑存在下述间歇失败，不标为稳定资格已证明 |

三个平台均成功生成，且全部 fixture 的原始 JSON 通过指定官方核心检查。
普通 SS、UoT v1/v2、HY2 四种场景及 AnyTLS 均完成回环数据传输。
既有五种 transport 完整重跑通过，但首次复跑曾在 VLESS HTTPUpgrade 上出现
一次 `unknown protobuf message header: 137` / 空 HTTP 响应；随后独立完整重跑
五种场景全部通过。成因尚未证实，因此 HTTPUpgrade 不能标为稳定运行资格已证明；
未添加自动重试，也未改写正式 transport 输出来掩盖该失败。

## 本轮实际修改文件（不含保留的 Stage B 未提交改动）

- 转换与 model：`Converters/{Shadowsocks,Hysteria2,AnyTls}Converter.cs`、
  `Converters/SourceFieldSchema.cs`、`Helpers/GoDurationHelper.cs`、
  `Models/Singbox/OutboundConfig.cs`。
- policy 与编排：`Configuration/{OutboundTuningPolicy,ControlPlaneDnsPolicy}.cs`、
  `Builders/{BuildArtifacts,SingboxConfigBuilder}.cs`、
  `Builders/Components/{ProfilePlanner,InboundBuilder,DnsProfileBuilder,RouteProfileBuilder}.cs`、
  `Services/{ConversionService,SingboxConfigValidator}.cs`。以上路径相对 `src/BoxForge.Core`。
- 测试：`Tests/BoxForge.Tests/BoxForge.Tests.csproj`、
  `{SemanticCoverage,ProxyConverter,ProfilePlanner,SingboxConfigBuilder,SingboxConfigValidator,ProtocolSourceMapping}Tests.cs`；
  fixtures：`shadowsocks.yaml`、`hysteria2.yaml`、`anytls.yaml`、`profiles.yaml`。
- 运行验证：`scripts/verify-shadowsocks.mjs`、`scripts/verify-session-protocols.mjs`。
- 文档：`docs/architecture.md`、`docs/configuration.md`、`docs/generated-config.md` 和本文件。
