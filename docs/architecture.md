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
2. `InputDiscovery` 解析物理路径、检查输入输出目录关系，并按文件名排序发现
   顶层 YAML；重复配置名和无效文件名在读取前报告。
3. `LocalGenerationWorkflow` 将每个文件作为一个 `ConversionRequest` 交给
   `IBoxForgeEngine`。
4. `BoxForgeEngine` 校验内存输入，调用 `ConversionService.Prepare` 解析
   YAML（重复键、循环引用及超限展开会直接失败）、生成 `NodeCatalog` 和稳定 `cache_id`，然后按
   Android、Linux、Windows 顺序调用 `ConversionService.Convert`。
5. 对每个目标平台，`SingboxConfigBuilder` 组合 inbound、endpoint、outbound、
   DNS、route 和 experimental 配置。
6. `SingboxConfigValidator` 检查 BoxForge 自身约束，`ConfigSerializer` 生成
   JSON 并计算小写十六进制 SHA-256。只有全部平台成功才返回
   `ConversionBundle`。
7. `OutputTransaction` 暂存本地产物并比较完整文本和文件集；所有输入和平台
   成功后，工作流才提交事务。提交失败时恢复旧输出；未提交的暂存在退出时清理。

## 内置策略数据

`BoxForge.Core/Configuration/default-profile.json` 作为程序集资源内嵌，保存现有地区
匹配表达式、服务定义、各平台参数及远端 rule-set URL 和更新间隔。
`ProfileDefinitions`、`PlatformProfile.For` 和路由构建器共用这份默认数据；加载器拒绝
未知字段、重复字段、不支持的版本及缺失的目标平台，不依赖运行目录中的文件。
地区匹配保持原有生成式正则的文化无关大小写行为。

地区、服务、rule-set 来源及 resolver 支持外部 JSON 覆盖（[地区](configuration.md#地区-profile)、
[服务](configuration.md#服务-profile)、[来源](configuration.md#rule-set-来源-profile)、
[resolver](configuration.md#resolver-profile)），ID 使用字符串。
每个 Core 容器加载一个不可变 `BoxForgeProfile`；节点名称校验、selector、DNS 服务
域名池、路由服务规则、rule-set 下载定义及 DoH 端点使用同一实例，避免不同容器的覆盖互相影响。
四类覆盖共用数组、对象键和标识唯一性检查，各类字段的合并规则分别保留。
保留 tag 由有效 profile 直接维护，与服务引用校验使用的规则集 tag 集合相互独立。
地区正则有 250ms 单次匹配上限，超时导致转换
失败，不返回部分结果。
现有环境变量开关及其默认值仍由原代码处理；路由阶段顺序、固定保留 tag 和
IPv6 守卫继续由代码约束。Android 顺序回退与桌面并行竞速的差异保持不变。
服务只能选择两个既有阶段；来源只允许按现有 tag 覆盖 HTTPS URL 和正更新间隔，
单 tag 从批量来源中拆出时不影响其他成员。resolver 只开放现有四个角色的规范 IPv4
地址及 TLS DNS 名称，引导 CIDR 同步更新，主备和 detour 固定。本阶段没有开放平台参数或
隐私档位覆盖，也没有 Schema。

`Tests/BoxForge.Tests/Fixtures/default-profile-sha256.json` 记录迁移前实际 CLI 输出的
SHA-256：5 份 YAML × 3 个平台 × 8 种桌面 Tailscale、Android Tailscale、API 开关
组合，共 120 份。回归测试对生成 JSON 的 UTF-8 字节计算哈希，包含格式与字段顺序；
维护策略时不能仅为通过测试而重写这些基线。

## 不可信 YAML 的资源边界

正常、无循环的 YAML anchor/alias 可以复用。解析与规范化最多支持 64 层递归，
全部代理共用 100,000 个规范化值的预算，展开后的键和字符串文本合计最多 16 MiB UTF-8
（包括 alias 的每次展开，所有代理共享同一预算）。文本上限避免少量大型 scalar
经重复 alias 在缓存 ID 序列化时膨胀成巨量数据。
映射和序列均按引用身份检查当前祖先链，循环引用在 schema 校验和缓存 ID 计算之前拒绝。
这些失败由引擎包装为转换错误，API 返回 422；不会等待栈溢出或内存耗尽。
限制不是可配置项；预算内的 scalar 值和无循环共享引用保持来源值。

## 目录职责

| 目录 | 职责 |
| --- | --- |
| `src/BoxForge.Core/` | 引擎契约与实现、模型、解析、协议转换、构建、校验和序列化 |
| `src/BoxForge.Cli/` | `Program`、命令行解析、本地文件工作流、日志、退出码和组合根 |
| `src/BoxForge.Server/` | HTTP 端点、请求限制、Problem Details 错误映射和组合根 |
| `Tests/BoxForge.Tests/` | 命令行、HTTP API、配置、构建器、顺序和校验器回归测试 |

## HTTP API 边界

Server 将 JSON 或单个 YAML 上传映射为 `ConversionRequest`，只在引擎完整返回
`ConversionBundle` 后构建响应或 ZIP。`content` 不会再次格式化，ZIP entry
路径由经过跨平台校验的配置名与固定平台目录组成。
Server 不访问订阅 URL、不保存输入或结果；上传缓冲和 ZIP 构建均在内存中，
同源页面不加载外部脚本或统计资源。当前没有鉴权和限流，不应直接公开服务。
请求、大小限制、错误契约和安全头的完整定义见[HTTP API](http-api.md)。

## 扩展代理协议

1. 在 `src/BoxForge.Core/Converters/` 实现 `IProxyConverter`，使
   `CanHandle` 只识别目标类型。
2. 在 converter 内部声明 `Schema`：列出已映射字段、输入别名、有明确理由忽略的字段，
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
避免把列表或对象转换成字符串；嵌套 Reality 和 Shadowsocks 插件对象也逐项检查。
嵌套声明可以附带使用条件；别名沿用同一嵌套 schema 和冲突检测。
VLESS/Trojan 只接受原生 TCP，旧传输选项作为不支持的来源语义明确拒绝。
`name`/`type` 等元数据和明确无连接作用的选项在 schema 中列明，
不会用通配规则吞掉未知字段。

引擎使用 strict conversion：任一不安全节点使整次转换失败，不返回部分配置。
non-strict 调用则跳过整个节点并记录警告，包括缺少 `type`、未知协议、字段映射
失败和保留 tag 冲突；不会删掉无法映射的字段后输出降级
节点。后续协议工作应把字段从 `UnsupportedSemantic` 改为 `Mapped`，同时提供
映射与反例测试，并重新核对锁定来源与目标核心的实际行为。

`OutboundTuningPolicy` 在 converter 之后、生成配置之前，只用 `??` 填入
缺失的 `connect_timeout`、桌面 TCP keepalive 和 Hysteria2 hop/BBR 值。
这些字段在 outbound model 中默认是 `null`；模型本身不施加 BoxForge policy。
路由 sniff 的 `300ms` 由 `RouteTuningPolicy` 明确命名。当前 YAML 显式
HY2 hop/BBR 值优先于调优。显式固定间隔不会补入调优的随机上限；显式带宽
启用时不会注入 BBR profile（显式 profile 仍保留，核心仅在 BBR 模式使用）。

协议相关职责集中在现有模块，不在 CLI 或 Server 中重复转换：

| 模块 | 职责 |
| --- | --- |
| `Converters` / `SourceFieldSchemas` | 协议选择、来源字段声明、值与组合校验、目标映射 |
| `TlsConfigHelper` | 共享 TLS、uTLS 与 Reality 映射 |
| `Models/Singbox` | 目标 JSON 形状与已支持的多态类型，不携带来源字段或调优策略 |
| `OutboundTuningPolicy` / `RouteTuningPolicy` | 仅对缺失值施加 BoxForge 调优 |
| `ConfigSerializer` | 序列化已校验模型，仅省略 null 和空集合 |

各协议字段、拒绝组合和输出默认值以[生成配置约定](generated-config.md)为准；
来源/目标核心版本、脚本使用方法和未验证路径以
[协议验证边界](protocol-validation.md)为准。

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
平台的 DualStack。Android 不输出未实现的 strict_route，仍保留 TUN 捕获地址、
AAAA 阻断及 IPv6 reject guards；本地静态验证不等于真机防逃逸抓包证明。

- 路由顺序由 `RouteProfileBuilder` 的阶段列表与前置约束声明；检查阶段顺序后，
  独立一遍展开 Linux 的 bypass/bridge 伴随规则。阶段说明生成
  [路由顺序文档](routing-and-dns.md#路由规则顺序)，并由测试检查同步；阶段元数据
  不进入目标 JSON。validator 独立检查实际规则，不能依赖生成器阶段标签。
- 输入文件按文件名排序，平台顺序固定为 Android、Linux、Windows。
- 引擎不依赖 `File`、`Directory`、`Path` 或环境输入输出目录；文件边界
  由 CLI 的 `InputDiscovery`、`OutputTransaction` 和 `LocalGenerationWorkflow`
  承担，分别负责发现与路径检查、输出事务、批次编排。
- 引擎先完成所有请求平台的转换，任一平台失败时不返回部分
  `ConversionBundle`。
- 同名 `.yaml`/`.yml` 会导致对应配置失败，避免不确定的输出覆盖。
- 内容比较使用完整文本；仅当内容与文件集同时相同时，整批才视为无变更。
- 替换时先将旧输出移到同级备份目录，再移入新输出；第二步失败时恢复备份。
  若恢复也失败，保留备份目录，并同时报告两次失败和旧输出的备份位置。
- 临时目录与备份目录的清理失败会记录警告，不会将已生效的新输出误报为失败。

## 校验边界

`SingboxConfigValidator` 只检查生成器特有约束：IPv6/AAAA 守卫、控制面策略、
显式 SNI 映射保真、等价指纹范围、缓存身份，以及标签和引用完整性。
来源字段、端口和认证材料在解析器和转换器中校验；目标语法与核心默认语义由
自动化的官方 `check` 覆盖，校验器不再维护一份目标核心格式规则。
outbound、endpoint 和 inbound 各自的 tag 必须唯一，outbound 与 endpoint
共享路由目标命名空间，tag 也不得互相重复。
内置校验器不会启动 sing-box 或下载核心。`scripts/verify-generated-configs.py`
在独立临时目录中生成 5 个 fixture × 3 平台 × Tailscale/API 开关两种状态的
60 份配置，交给固定版本和 revision 的官方核心检查；下载模式同时校验固定
归档 SHA-256。普通 CI 和发布前验证使用同一脚本。部署实际配置时的核心检查和
运行时验证仍是独立步骤。

[返回 README](../README.md)
