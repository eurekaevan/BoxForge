# 架构与扩展

BoxForge 由 Core 类库、CLI 可执行项目、Server 可执行项目和一个
测试项目组成。CLI 和 Server 都通过 Core 中的 `IBoxForgeEngine`
传入 YAML 文本、配置名和目标平台。

## 项目与依赖

```text
BoxForge.Cli    ──→ BoxForge.Core
BoxForge.Server ──→ BoxForge.Core
BoxForge.Tests  ──→ BoxForge.Cli, BoxForge.Server, BoxForge.Core
```

- `BoxForge.Core` 是 Class Library，不引用 CLI，也不依赖
  `Microsoft.Extensions.Hosting`。
- `BoxForge.Cli` 是 Executable，引用 Core；它通过 `IBoxForgeEngine`
  调用转换能力。
- `BoxForge.Server` 是 ASP.NET Core Executable，只引用 Core；它不引用
  CLI，不使用解析器、构建器、`ConversionService` 或文件工作流。
- `BoxForge.Tests` 引用 Core、CLI 和 Server，保留核心构建、命令行、
  本地事务工作流和 HTTP API 的回归测试。

## 处理流程

1. `GenerateCommandParser` 解析 `generate` 子命令、路径和平台。
2. `LocalGenerationWorkflow` 校验路径，并按文件名排序读取顶层 YAML。
3. `LocalGenerationWorkflow` 将每个文件作为一个 `ConversionRequest` 交给
   `IBoxForgeEngine`。
4. `BoxForgeEngine` 校验内存输入，调用 `ConversionService.Prepare` 解析
   YAML（重复键会直接失败）、生成 `NodeCatalog` 和稳定 `cache_id`，然后按
   Android、Linux、Windows 顺序调用 `ConversionService.Convert`。
5. 对每个目标平台，`SingboxConfigBuilder` 组合 inbound、endpoint、outbound、
   DNS、route 和 experimental 配置。
6. `SingboxConfigValidator` 检查 BoxForge 自身约束，`ConfigSerializer` 生成
   JSON 并计算小写十六进制 SHA-256。只有全部平台成功才返回
   `ConversionBundle`。
7. 本地产物先写入临时目录；所有输入和平台成功后，工作流才
   替换输出目录。

## 目录职责

| 目录 | 职责 |
| --- | --- |
| `src/BoxForge.Core/` | 引擎契约与实现、模型、解析、协议转换、构建、校验和序列化 |
| `src/BoxForge.Cli/` | `Program`、命令行解析、本地文件工作流、日志、退出码和组合根 |
| `src/BoxForge.Server/` | HTTP 端点、请求限制、Problem Details 错误映射和组合根 |
| `Tests/BoxForge.Tests/` | 命令行、HTTP API、配置、构建器、顺序和校验器回归测试 |

## HTTP API 边界

- `GET /healthz` 只返回服务状态。
- `POST /api/v1/convert` 将 JSON 请求映射为 `ConversionRequest`，并将
  `ConversionBundle` 直接映射为响应；`content` 不会被再次序列化或格式化。
  响应 `path` 使用与 ZIP 导出相同的配置名限长和路径字符校验。
- `POST /api/v1/export` 只在内存中读取一个 YAML 上传，引擎完整
  转换成功后，再按 Android、Linux、Windows 顺序构建 ZIP。
  ZIP entry 路径由经过限长和路径字符校验的配置名与固定
  平台目录组成，上传文件名不能控制完整 entry 路径。
- `GET /` 和同源静态资源提供原生 HTML/CSS/JavaScript 上传页面，
  没有外部 CDN、字体、统计或第三方脚本。
- Server 不读写配置目录、不访问订阅 URL，不保存请求或结果，
  且默认不启用 CORS。
- 请求体上限为 4 MiB，`yaml` UTF-8 字节上限为 2 MiB。参数
  错误返回 400，引擎的稳定转换失败返回 422，过大请求返回
  413，未预期异常返回 500；错误响应不包含输入 YAML、节点信息
  或异常堆栈。
- Server 将 multipart 内存缓冲阈值设为请求上限，允许范围内的上传
  不会溢出到临时文件。所有响应使用 `no-store`，并设置限制
  外部资源的 CSP、`nosniff` 和 `no-referrer`。
- 当前 API 没有鉴权、限流和 SSRF 防护，尚不应直接暴露到公网。

完整的请求、响应、ZIP、大小限制和错误码契约见
[HTTP API](http-api.md)。

## 扩展代理协议

1. 在 `src/BoxForge.Core/Converters/` 实现 `IProxyConverter`，使
   `CanHandle` 只识别目标类型。
2. 声明 `SourceSchema`：列出已映射字段、输入别名、有明确理由忽略的字段，
   以及尚不能安全映射的连接语义。公共字段和 TLS/Reality 字段复用
   `SourceFieldSchemas`，协议不支持的能力单独标为 unsupported。
3. 将 Clash 字段校验和 sing-box outbound 创建放在该转换器内。
4. 在 `CoreServiceRegistration.AddBoxForgeCore` 中注册新转换器。
5. 增加有效转换和无效字段的单元测试；如果引入新引用类型，同时扩展
   `SingboxConfigValidator`。

### 来源语义与调优优先级

节点出站的取值顺序为：YAML 显式值 → 精确语义映射 → BoxForge tuning →
sing-box 运行时默认值。BoxForge 不保证逐字段原样复制；它保证已声明的显式
来源语义不会被 tuning 静默覆盖。来源未指定时，BoxForge 可以施加自己的调优。

`ProxyConverterBase.Convert` 在协议映射前逐项检查 `ClashObject.Properties`。
converter 拥有自己的字段声明；`NodeCatalogBuilder` 只负责选择 converter 和
处理转换结果。已知但未支持的连接字段、尚未分类的字段、冲突的别名和无效的
显式值都会形成包含协议、节点名、字段名及原因的错误。schema 同时声明值形状，
避免把列表或对象转换成字符串；嵌套 Reality、WS、gRPC 对象也逐项检查。
嵌套声明可以附带使用条件，拒绝不属于当前 `network` 的选项；别名沿用
同一嵌套 schema 和冲突检测。HTTP header 名称是开放键，但每个值必须是
字符串，并单独检查大小写冲突和握手保留字段，不能把列表扁平化。
`name`/`type` 等元数据和明确无连接作用的选项在 schema 中列明，
不会用通配规则吞掉未知字段。

引擎使用 strict conversion：任一不安全节点使整次转换失败，不返回部分配置。
non-strict 调用则跳过整个节点并记录警告；不会删掉无法映射的字段后输出降级
节点。当前支持 VLESS TCP/WS/受限基础 gRPC、Trojan TCP/WS、两者的
无 fast-open HTTPUpgrade，以及 shared TLS ALPN。具体输入限制见
[生成配置说明](generated-config.md#transport-与-shared-tls)。重点登记的 gap 包括
Trojan gRPC、HTTP/H2/xHTTP、复杂 WS/gRPC 参数、TLS 证书校验扩展、
Hysteria2 QUIC/Realm 扩展、AnyTLS disable-reuse，
以及 Shadowsocks 的复杂插件语义。后续协议工作应把字段从
`UnsupportedSemantic` 改为 `Mapped`，同时提供映射与反例测试。

`OutboundTuningPolicy` 在 converter 之后、生成配置之前，只用 `??` 填入
缺失的 `connect_timeout`、桌面 TCP keepalive 和 Hysteria2 hop/BBR 值。
这些字段在 outbound model 中默认是 `null`；模型本身不施加 BoxForge policy。
路由 sniff 的 `300ms` 由 `RouteTuningPolicy` 明确命名。当前 YAML 显式
HY2 hop/BBR 值优先于调优。显式固定间隔不会补入调优的随机上限；显式带宽
启用时不会注入 BBR profile（显式 profile 仍保留，核心仅在 BBR 模式使用）。

Hysteria2 带宽按当前 Mihomo 的整数单位语法解析，大小写区分 bits/bytes，
只接受可精确表示为目标整数 Mbps 的速率；例如 `100 Kbps`、小数速率或溢出
报错，不会截断为 Mbps。跳端口支持整数秒及整数范围，按源行为排序并应用
最小 5 秒/零值 30 秒；`20s` 不是当前 Mihomo 的合法输入。
Gecko 独立保留密码及包大小，0 等价于缺失，默认 512/1200，有效范围
为 `1 <= min <= max <= 2048`，仅 gecko 可指定。目标没有等价字段的 UDP MTU、
握手超时、源 QUIC receive-window 与 Realm 拓扑仍明确拒绝；HY2 uTLS 不支持。

`V2RayTransport` 是独立的目标 JSON 多态模型，只注册已实现的 `ws`、
`grpc`、`httpupgrade`；VLESS/Trojan 共享 `transport` 属性类型。
`TransportConfigHelper` 负责两端语义转换，`TlsConfigHelper` 保留 source ALPN
的顺序、重复项和值；模型不包含 Mihomo 字段，也不自带 transport tuning。
无 transport 的 TCP 出站省略该属性，不生成虚假的 `type: tcp`。
validator 只补充 BoxForge 的 model/必填映射值约束（`SB079`），目标 schema
合法性仍由官方核心检查。

Mihomo 的 [UoT 默认版本为 1](https://wiki.metacubex.one/en/config/proxies/ss/)，
而 [sing-box 布尔形式默认版本为 2](https://sing-box.sagernet.org/configuration/shared/udp-over-tcp/)；
因此生成对象形式的 `udp_over_tcp`，显式保留版本：未指定版本或源版本 0 使用 1，
版本 1/2 保持原值；关闭或未启用时省略。无效版本及重复别名在转换阶段拒绝。
Shadowsocks 插件选项必须为 Mihomo 对象，由插件专用 schema 和 mapper 翻译，
不接受原始 SIP003 字符串或通用对象扁平化。`obfs` 的 http/tls 模式映射到
`obfs-local`，显式物化源默认 host `bing.com`。`v2ray-plugin` 仅支持
websocket、普通绝对路径、显式 `mux: false`、可选标准 TLS 的子集；自定义 headers、
证书、指纹和 HTTPUpgrade 等无法精确表达的选项拒绝。SIP003 值按其语法转义。
源 Shadowsocks 没有 network 字段，不能把该字段映射为目标网络限制。

AnyTLS 的 timeout/check-interval 共用正 Go duration 解析器，数字按秒，校验
int64 纳秒上限和亚纳秒零值；duration 文本的受支持子集限制为最多 128 字符，
避免对不可信订阅进行无界大整数解析。保留已有 duration 扩展输入。Mihomo 原生字段为
整数秒，两端会话库均将不超过 5 秒的维护间隔/超时恢复为 30 秒默认值。
`min-idle-session` 保留非负整数（目标模型上限 int32），仅映射保留数量，
不声称两端会话池的过期时间刷新算法完全一致。`client-metadata` 只接受字符串，
原样保留空白、Unicode 与 `=`；拒绝 LF 和超过 65479 UTF-8 字节的值，以免
注入 newline-delimited settings 或溢出 uint16 frame 长度。AnyTLS Reality 和
目标未暴露的 disable-reuse 继续拒绝。

## 确定性与替换边界

`ProfilePlanner` 的地区 inventory 记录实际 leaf membership，与地区 selector
是否生成分离；服务的 `DefaultRegion` 对应 0/1/2+ leaf 时分别选择主组/leaf/
地区 selector。主组沿用已生成 US 组优先、否则首个地区组/首 leaf/DIRECT 的
文档行为，不把单 US leaf 当作新主组策略。

`SingboxBuildRequest.AddressFamily` 在创建请求时从平台 policy 取得，正式流程
三个平台仍是 Ipv4Only。同一个值传给 DNS、route、控制面 resolver 与
`Validate(config, addressFamily)`，validator 不再次推导平台。IPv4-only 除解析
策略和 IPv6 节点字面量外，还验证 early/post-resolve IPv6 reject guards 和首条
无条件 AAAA 空 NOERROR；不能用缩窄到特定网络/域名的规则伪装全局 guard。
DualStack 测试显式传入 policy，不要求上述 IPv4-only 限制；没有开启任何实际
平台的 DualStack。Android 不再输出未实现的 strict_route，仍保留 TUN 捕获地址、
AAAA 阻断及 IPv6 reject guards；本地静态验证不等于真机防逃逸抓包证明。

- 输入文件按文件名排序，平台顺序固定为 Android、Linux、Windows。
- 引擎不依赖 `File`、`Directory`、`Path` 或环境输入输出目录；文件边界
  只存在于 `LocalGenerationWorkflow`。
- 引擎先完成所有请求平台的转换，任一平台失败时不返回部分
  `ConversionBundle`。
- 同名 `.yaml`/`.yml` 会导致对应配置失败，避免不确定的输出覆盖。
- 内容比较使用完整文本；仅当内容与文件集同时相同时，整批才视为无变更。
- 替换时先将旧输出移到同级备份目录，再移入新输出；第二步失败时恢复备份。
- 临时目录与备份目录的清理失败会记录警告，不会将已生效的新输出误报为失败。

## 校验边界

`SingboxConfigValidator` 检查标签、引用、必填字段、端口和生成器特有约束。
outbound、endpoint 和 inbound 各自的 tag 必须唯一，outbound 与 endpoint
共享路由目标命名空间，tag 也不得互相重复。
它不会启动 sing-box，也不会下载或验证远程 rule-set。发布流程应对每个最终
`config.json` 另行执行目标版本的 `sing-box check`。

[返回 README](../README.md)
