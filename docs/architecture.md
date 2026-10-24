# 架构与扩展

BoxForge 由 Core 类库、CLI 可执行项目、Server 可执行项目和一个
测试项目组成。CLI 和 Server 都通过 Core 中的 `IBoxForgeEngine`
传入 YAML 文本、配置名、目标平台和可选的请求 profile。

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
  CLI，不使用解析器、构建器或文件工作流。
- `BoxForge.Tests` 引用 Core、CLI 和 Server，保留核心构建、命令行、
  本地事务工作流和 HTTP API 的回归测试。

## 处理流程

1. `GenerateCommandParser` 解析命令。`profile show --resolved` 加载配置并直接输出快照；
   `generate` 解析路径和平台后继续下面的生成流程。
2. `InputDiscovery` 解析物理路径、检查输入输出目录关系，并按文件名排序发现
   顶层 YAML；重复配置名和无效文件名在读取前报告。
3. `LocalGenerationWorkflow` 将每个文件作为一个 `ConversionRequest` 交给
   `IBoxForgeEngine`。
4. `BoxForgeEngine` 是唯一的编译编排入口：校验内存请求，解析各平台配置文档，
   只解析一次 YAML（重复键、循环引用及超限展开会直接失败），生成不依赖平台的
   `NodeCatalog` 和规范化节点内容派生的 `cache_id`。
5. 按 Android、Linux、Windows 顺序，`ConfigurationPlanner` 将节点绑定到该平台的
   `ResolvedProfile`，检查保留名称并确定完整策略计划；`SingboxConfigBuilder.Build`
   集中校验完整计划，再发射为 inbound、endpoint、outbound、DNS、route 和 experimental 配置。
6. `SingboxConfigValidator` 检查 BoxForge 自身约束，`ConfigSerializer` 生成
   JSON 并计算小写十六进制 SHA-256。只有全部平台成功才返回
   `ConversionBundle`。
7. `OutputTransaction` 暂存本地产物并比较完整文本和文件集；所有输入和平台
   成功后，工作流才提交事务。提交失败时恢复旧输出；未提交的暂存在退出时清理。

## 内置策略数据

`BoxForge.Core/Configuration/default-profile.json` 作为程序集资源内嵌，保存现有地区
匹配表达式、服务定义、运行时开关、全局策略、各平台参数及远端 rule-set URL 和更新间隔。
默认数据由 `BoxForgeProfile` 加载为带有覆盖层的不可变配置文档并注入；加载器拒绝
未知字段、重复字段及缺失的目标平台，不依赖运行目录中的文件。
地区表达式在解析时验证并构造正则，保持文化无关的忽略大小写行为。

profile 根分区为 `features`、`dns`、`route`、`tuning`、`privacy`；`platforms`
是按 Android、Linux、Windows 选择的同结构稀疏层，不允许嵌套 `platforms`；根与平台项均不接受 `version` 字段。
对每个目标先合并内置根值、文件根值、统一环境根值，再依来源顺序应用平台层；
请求根值及请求自身的目标平台层最后应用。宿主快照启动时加载，请求快照彼此隔离。
`BoxForgeProfile.Resolve(platform, request)` 在构建前一次性应用覆盖层，返回绑定单个平台的
`ResolvedProfile`。文档上的分区属性表示根值；已解析配置保存最终分区、平台参数、
地区匹配表达式与保留名称，不支持继续叠加或切换平台。节点名称检查和策略规划只接收已解析配置；来源解析不接收平台配置。
目标构建器只接收计划和缓存 ID，不持有宿主文档，也没有可选配置回退。
无状态的节点、DNS、路由和 Tailscale 构建组件使用静态调用。

`profile show --resolved` 返回各平台完整五分区快照，根与平台项均无 `version` 字段，
不作为可直接重新加载的差异覆盖文件。

`features` 保存 Tailscale/API，`dns` 保存地址族、模式、超时、缓存和 resolver，
`route` 保存地区、服务、规则集来源、sniff 与 TUN/转发策略，`tuning` 保存连接及探测参数。
`privacy` 保留空对象，不引入开关。所有生成模块使用该次转换的有效目标配置，
不会重新读取环境变量或回退到静态平台默认值。

数组路径覆盖使用显式操作，固定顺序为 `replace`、`append`、`remove`、`patch`、`set`、
`remove_by_key`、`insert_before`。`remove` 按原始完整 JSON 值匹配；按键操作必须唯一定位现有项，
`patch`/`set` 保留位置。Schema 同时校验部分修改与完整实体，未选中平台的操作也需通过结构校验。
没有按 ID/tag 隐式合并或批量来源自动拆组，具体格式见[数组操作](configuration.md#数组操作)。
规则源 URL 模板展开与更新时长归一化在路由规划时进行；profile 快照和后续叠加
保留原始 URL、时长文本，`remove` 不使用规范化后的发射值。
操作 payload 中完整实体的内部数组是普通数据，例如服务 `rule_sets`、来源 `tags`。
地区、服务、来源及 resolver 的字段约束见[配置参考](configuration.md)，最终标签唯一性、
必需来源及跨模块引用仍校验。地区正则具有 250ms 单次匹配上限，超时使请求失败。

除引导文件路径的 `Profile` 外，`BOXFORGE_` 环境键必须映射真实配置路径；
键名不区分大小写，`__` 分隔层级，标量与数组操作 JSON 都按目标类型严格校验。
旧开关、未知键、重复键及不允许的 null 均拒绝。
环境前缀校验由 CLI、Server 加载配置时执行，Core 只解析注入的 `IConfiguration`。
普通宿主的同名业务节不会被当成旧 profile 数组；profile 文件和请求仍严格拒绝未知根字段。
未提供覆盖时使用内置 profile；不提供隐私档位或 JSON Schema。

`Tests/BoxForge.Tests/Fixtures/default-profile-sha256.json` 记录内置 profile 实际 CLI 输出的
SHA-256：5 份 YAML × 3 个平台 × 8 种桌面 Tailscale、Android Tailscale、API 开关
组合，共 120 份。回归测试对生成 JSON 的 UTF-8 字节计算哈希，包含格式与字段顺序；
策略变更时须核对实际输出差异，再同步更新这些基线。

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

### 内部编译流水线：协议解释、catalog 与配置计划

VLESS、Trojan、AnyTLS、Hysteria2 与 Shadowsocks 共用 `ProxyNodeParser` 入口，
按协议 Schema 校验后调用解释钩子，构建中立节点。协议、catalog 和各层
计划均属于内部实现；对外转换使用 `IBoxForgeEngine`：

```text
ClashProxyNode → ProxyNodeParser → Schema + 协议解释 → NodeCatalog（节点 IR + 名称）
                                      ↓
         ProfilePlanner / DnsProfileBuilder.Plan / RouteProfileBuilder.Plan
                                      ↓
                  组、DNS 与路由计划 IR + 节点平台调优
                                      ↓
           ConfigurationPlan → 各计划不变量与跨模块引用检查
                                      ↓
      SingboxNodeEmitter / GroupEmitter / DnsEmitter / RouteEmitter
                                      ↓
        既有 TUN / endpoint / service 组合 → 完整配置校验 → JSON
```

五个协议的节点 IR、共用 `TlsSettings` 和 `RealitySettings` 不引用 sing-box 模型或 JSON 属性。
flow 与 UDP 编码使用语义枚举，其他字段直接保存解释后的值。
只有强制 TLS 协议的激活开关及 Gecko 包大小校验需要区分缺失和显式 null，
这些字段使用包含值与 `IsSpecified` 的只读 `SourceValue<T>`；不保存字段名、字段路径或证据 ID。
字段声明和别名由协议 Schema 校验，含固定 revision 的语义证据由数据表维护。
SNI 缺失、null 或空白时仍回退到 server；ALPN 缺失与显式空列表在 IR 中不同，
目标序列化仍维持原有空列表省略行为。

Trojan 省略 `tls` 仍启用协议要求的 TLS，IR 中保留来源缺失与解释后的 TLS 激活状态；
显式 true 与省略保持可区分，false 或 null 仍由原 schema 拒绝，不静默改成 true。
`Validate` 检查节点 TLS 激活条件，共用 TLS 检查要求 Reality 具有客户端指纹；
发射入口执行检查，内部 IR 矛盾直接失败，不作为可跳过的来源节点错误吞掉。

AnyTLS 的 idle timeout、检查间隔、最少空闲会话数及 metadata 保存解释后的可空值。
缺失与显式 null 均解释为 null，最终省略；显式零会话数和空 metadata 保留。
时长复用既有正 Go duration 解析器，数字秒补 `s`，其他有效文本保留，避免引入
精度或输出字节变化。TLS 仍为协议必需，不接受 Reality。发射入口还检查正时长、
非负会话数，以及 metadata 的 LF 和 65479 UTF-8 字节边界；不会自动添加客户端身份。
AnyTLS 原有错误顺序保持：server → port → password → 会话参数 → metadata → TLS。

Hysteria2 使用结构化端口项，保留标量端口与显式范围的区别（包括 `443-443`），
维持 `ports` 优先、列表顺序和重复项，不展开范围。跳转间隔用秒数和可空上界表达，
保留固定与随机范围之别；显式空值仍为固定 30 秒，缺失/null 继续交由既有调优处理。
带宽保留当前精确整数 Mbps 支持范围，不舍入或截断；BBR 和混淆类型使用枚举。
Gecko 参数保留是否显式提供的标记，0/null 均解释为使用核心默认值，内部检查仍使用 512/1200
默认边界；混淆密码保留原始值。QUIC TLS 必需且不接受 Reality/uTLS。
平台调优在发射前填入独立的 IR 调优值，保留原始解析值和显式标记；
显式固定间隔不补随机上界，正带宽不补 BBR。
原错误顺序保持：server → ports → Gecko/obfs → hop → bbr → up/down → password → TLS。

多项 `ports` 中的标量端口由后端输出成等端点范围：`443,8443-8444` 生成
`server_ports: ["443:443", "8443:8444"]`，修复锁定核心拒绝裸 `443` 的问题。
单一标量仍生成 `server_port: 443`，显式 `443-443` 仍使用范围形式；IR 中的原始
标量/范围区别、顺序及重复项保留。官方验证脚本在临时 fixture 副本中追加端口回归样本，
覆盖三平台与全部 Tailscale/API 开关组合，不修改仓库 fixture 或输出字节基线。

Shadowsocks 的 cipher/method、UoT 开关/版本与插件保留解释后的值。
UoT 省略、null 和版本 0 均解释为 v1，禁用时仍省略整个目标对象；版本 1/2
用语义枚举表达。obfs 的 http/tls 模式同样使用枚举，host 保留原始文本，
缺失/null 回退到 `bing.com`，两种嵌套别名仍由 Schema 严格校验。
后端才执行 SIP003 转义并生成 `obfs-local` 参数；前端不保存目标插件字符串。
纯空白或包含控制字符的 host、未知插件、未支持的指纹和原始 SIP003 选项字符串仍拒绝。
密码保留原始空白。原错误顺序保持：server → port → cipher → password → UoT → host → mode。

来源/目标解释、固定 revision 证据和映射状态统一保存在
[`docs/data/protocol-mappings.json`](data/protocol-mappings.json)，不再编译进运行时。
`generate-protocol-mappings.py` 生成[协议映射证据](protocol-mappings.md)，`--check` 检查
文档是否过时；测试从同表生成用例，检查完整性、唯一 ID、固定 revision 和真实 Schema
字段声明，业务行为仍由独立回归测试检查。Trojan 的 `tls` 开关是 BoxForge
兼容输入，证据明确区分它与 Mihomo 原生 Trojan 的固有 TLS。
`Mapped` 表示已实现，不代表运行时等价证明。
共用 TLS 解析先产生中立设置；后端为每次输出复制 ALPN 和 Hysteria2 端口列表，
避免输出修改污染共享 IR。

内置五个协议在 catalog 中保留中立节点；规划仅使用节点名称，不提前发射目标模型。
连接超时、平台 keep-alive 和 Hysteria2 hop/BBR 默认值在 IR 副本上施加，
然后统一校验并发射；同一个 catalog 可以供多个平台并行构建，来源节点不变。
`ProxyNode` 和 `ConnectionSettings` 只承载当前五种节点共有的数据与调优值。

`NodeCatalog` 仅保存中立节点与名称，没有兼容目标模型视图或按访问历史切换的编译路线。
每次构建都从节点 IR 调优并发射独立的目标模型，哈希或读取属性不会改变后续构建行为。

组策略已使用中立的 `ProfileGroupPlan`，以 `SelectionGroupPlan` 表达候选项与默认项，
以 `ProbeGroupPlan` 表达 leaf 探测及其策略参数。`ProfilePlanner.PlanGroups` 只产生这些
计划，并填入平台探测间隔；规则集 HTTP client 的 detour 直接取自计划，不提前生成目标组。
IR 检查默认项属于候选列表、引用目标存在、探测组只含至少两个 leaf；
完整配置的标签唯一性、重复候选等检查仍由既有 validator 执行。
`SingboxGroupEmitter` 再复制候选列表生成目标组。地区、主组和服务的选项顺序与默认项不变。

DNS 规划同样先产生中立的 `DnsPlan`：`DnsHttpsResolverPlan` 记录解析器地址、TLS 名称和
连接出口，`DnsResolverPoolPlan` 记录池名称、匹配范围和主/备解析器。计划保留地址族、
选择模式、Tailscale 开关、广告规则集、最终解析器及服务/domestic/global 的顺序；
每个服务独立生成池及经过该服务选择组的主/备解析器，服务顺序与业务路由一致，
所有服务池仍位于 domestic 之前；无服务时只生成 domestic/global。
展开后的 `Rules` 是输出的唯一权威表示；`Pools`、`BlockedRuleSets` 和选择模式是待核对的策略说明，不能单独用来解释实际输出。
发射前检查解析器和池名称唯一、解析器引用有效、
有范围的池位于唯一的全局池之前，以及广告规则集非空，避免生成无条件负响应。
完整配置中的 detour、rule-set 等跨模块引用继续交给既有 validator。

`DnsProfileBuilder.Plan` 将池展开为不可变、有序的 `DnsRulePlan`，确定
顺序回退或正响应竞速、A 查询隔离、NXDOMAIN 接受顺序、response 标签、缓存行为、
终止 SERVFAIL、Auto AAAA 守卫及 Tailscale 路径。服务 resolver 的 detour 在规划时
绑定服务选择组，保留现有优先级。IPv4 正响应竞速仍不混入域名 rule-set 条件，
避免 sing-box 的 OR 语义改变匹配范围。`SingboxDnsEmitter` 只映射规则和值，
不选择匹配范围、执行顺序或回退算法。
计划校验核对前缀中的 AAAA 守卫、Tailscale 和广告拒绝规则，并检查池与求值规则的数量、
顺序、主备解析器、查询范围、响应标签、超时和缓存开关一致。服务池还必须绑定对应业务出口。
这些检查不重新运行规划器，也不等同于证明整个响应竞速算法正确；响应处理顺序由行为测试约束，官方核心检查目标配置的格式兼容性。

`RouteProfileBuilder.Plan` 产生带阶段标记的有序 `RouteRulePlan`，在规划时确定
逻辑 AND/OR 条件、sniff 范围和超时、早晚 IPv6 守卫、服务与国内分流优先级，
并在对应阶段展开 Linux bypass/bridge 伴随规则。`RoutePlan` 还保存规则源、最终出口
及控制面绑定。计划只使用 IR 类型；不引用 profile DTO 或 sing-box 模型。
发射前检查阶段与规则顺序、必需规则源、实际规则引用和服务唯一性。
完整阶段列表不能代替实际规则：校验要求 DNS 劫持位于首条，精确保留 TUN 范围及协议/端口条件，
早晚地址族守卫符合 Ipv4Only／ Auto／ DirectDualStack／ DualStack 策略（DirectDualStack 还要求服务规则集和最终出口之前的 IPv6 拒绝），必要固定阶段存在规则，启用 Tailscale 时保留其控制面路径。

`SingboxRouteEmitter` 将规则逐项映射到目标字段，不按阶段重新推导策略。
DNS 与路由发射均为每次输出创建独立的规则、嵌套条件与列表；修改输出不会污染计划。
同一计划可重复或并发发射。跨模块引用在汇总计划与完整目标配置中分别检查。

`ConfigurationPlan` 汇总地址族、DNS 选择模式、探测间隔等策略值、`TunPlan` 地址与开关、
完成平台调优的节点 IR、组/DNS/路由计划、外围开关与 `HttpClientPlan` 控制面绑定。
计划使用自己的类型和值快照，不保留 profile DTO 或兼容目标模型视图。
发射前检查组的 leaf 声明确实存在于 catalog、路由服务和最终出口、DNS detour 与规则集、
bootstrap resolver、HTTP client 的出口和解析器，以及 dashboard 的 HTTP client 绑定。
同时检查各计划的平台策略一致，并要求 Tailscale DNS/路由引用的 endpoint 已声明。

`ConfigurationPlanner.Plan` 汇总完整策略，`SingboxConfigBuilder.Build(plan, cacheId)`
在唯一的完整计划校验入口检查局部约束与跨模块引用，通过后才生成目标配置。
组件 planner 只构造计划，组件 emitter 只映射目标模型；独立的局部 `Validate` 入口用于按模块检查计划。
`DomainResolverPlan` 明确保存路由、HTTP client 和受来源 IPv4 约束的
节点解析器：服务器、地址族及 optimistic 缓存开关均已确定，发射只转换模型。
TUN、endpoint 与 API service 使用计划中保存的地址、开关和 Taildrop 目录，
发射时不重新读取宿主配置，也不再次规划。
完整目标 validator 继续检查标签重复、目标模型内部字段及地址族守卫，
汇总校验检查以下不同层次，不能将“通过校验”理解为所有产品行为已获证明：

- **结构**：枚举、字段、逻辑树和规则数组合法。
- **引用**：出口、解析器、规则集、HTTP client 和响应标签已声明且顺序有效。
- **必要策略**：DNS 劫持、地址族守卫、来源 IPv4 节点的独立 resolver 绑定及路由默认 resolver 符合约定。
- **产品一致性**：解析池说明与实际求值规则、服务 DNS 与业务出口一致；固定阶段具有实际规则。

嗅探器完整组合、全部响应竞速分支和服务优先级等仍由行为测试及输出基线约束，不能仅凭阶段存在断言产品策略完整。

配置文档在构造时解析所有平台进行验证，转换时仍会重新解析请求平台；当前没有平台快照缓存。
取消检查位于转换开始及各平台编译之前，不能视为解析、规划、发射全过程的有限工作预算。
现有来源大小、展开数量和正则超时各自约束局部工作；不承诺统一的编译截止时间。
官方 `check` 继续由验证脚本执行，不改变 CLI/Server 运行时依赖。
现有 120 份输出字节基线及协议拒绝测试共同守住兼容性。

### 诊断与校验归属

编译器诊断共用 `ConfigDiagnostic(Code, Path, Message)`。来源错误由
`NodeParseException.Diagnostics` 提供，profile 错误由继承 `FormatException` 的
`ProfileValidationException.Diagnostics` 提供，计划与目标校验使用
`ConfigValidationException.Diagnostics`。引擎的转换异常保留原有分类，并转交已知错误的诊断。
诊断集合是不可变快照，调用方修改原始列表不会改变异常中保存的结果。
引擎按 Android → Linux → Windows 的固定顺序处理请求平台。为保持既有异常契约，
首个请求平台的保留名称冲突包装为 `BoxForgeConversionException`，后续平台冲突包装为
`BoxForgePlatformConversionException`；两者都转交安全诊断。此例外只用于异常分类，
来源仍只解析一次，失败不返回部分产物，由引擎回归测试固定边界。

| 错误码 | 归属与含义 |
| --- | --- |
| `SRC001` | 来源 YAML 语法、结构、资源边界，或无法精确归属字段的节点错误 |
| `SRC002` | 已声明协议字段的类型、语义或协议类型错误 |
| `PF001` | profile 文档、覆盖操作或最终平台配置无效 |
| `IR001` | 汇总计划与平台策略不一致 |
| `IR002` | 引用了 catalog 中不存在的 leaf |
| `IR003` | 路由、DNS 或 HTTP client 引用了未声明的出站/endpoint |
| `IR004` | Tailscale DNS/路由需要的 endpoint 未声明 |
| `IR005` | 引用了未声明的规则集 |
| `IR006` | 引用了未声明的解析器 |
| `IR007` | 规则集下载或 dashboard 引用了未声明的 HTTP client |
| `IR008` | 局部计划的字段值、结构或资源边界无效 |
| `IR009` | 声明缺失、重复或名称冲突 |
| `IR010` | 阶段顺序、首选目标或求值依赖无效 |
| `IR011` | 必要策略缺失，或策略说明与实际规则、解析器绑定不一致 |
| `SB*` | 发射后的引用、字段与安全不变量复核，沿用既有目标错误码 |

来源路径使用 `source.proxies[索引]`，已声明字段可继续定位到嵌套字段；
未知键或无法可靠定位的错误停在已知节点/文档层，不从异常文本推测字段。
profile 的文件、宿主、直接覆盖与请求分别使用 `profile.file`、`profile.host`、
`profile.overlay`、`profile.request`；请求平台层位于其 `platforms.<平台>` 下。
保存的平台层按应用顺序定位为 `profile.platform_layers[索引].<平台>`，第 0 层是内置平台层。
最终语义错误位于 `profile.effective.<平台>`；这是无效最终值的位置，并非每个值的完整来源追踪。
数组操作包含 payload 索引，例如 `profile.request.route.services.patch[0]`；未知键使用 `[unknown]`，
不会将键名、选择器值或文件系统路径写入诊断。

组、DNS、路由各自收集局部诊断，汇总计划再按固定模块、字段和集合顺序检查跨模块引用，
一次抛出结构化异常。节点解析器映射按键的 ordinal 顺序使用数值索引，避免名称进入路径。
诊断消息不回显节点名、tag、输入值或凭据；来源异常的旧 `Message` 和内部异常仍可能包含
输入上下文，调用方应使用 `Diagnostics` 获取安全定位信息，而非公开完整异常。

引擎沿用既有错误包装，不返回部分平台产物；CLI 保留整批输出丢弃行为。
profile 无效仍按原有无效配置路径处理。HTTP 的 JSON 和 ZIP 接口继续使用通用
`400`/`422` 响应，不公开诊断、节点名称、凭据或部分产物。
真正的未预期错误不通过捕获任意异常伪装成输入错误。
完整目标 validator 独立复核标签重复、字段合法性与地址族守卫，不能以计划校验替代。
节点 `SourceValue` 只保存值及显式标记，语义数据表继续保存来源字段及固定源码证据。

### 现有协议扩展入口

五个协议共用 `ProxyNodeParser` 的单一解析入口；各 `*Protocol` 声明 `Schema` 并提供
解释已校验字段的钩子。路径固定为名称与来源字段校验 → 协议解释 → 节点 IR →
调优与发射，不再分别维护 converter 和 frontend 两条入口。

扩展协议时，在协议 `Schema` 声明已映射字段、别名、明确忽略的元数据及尚不能映射的
连接语义。公共字段与 TLS/Reality 声明复用 `SourceFieldSchemas`；协议解释不组装
sing-box 模型。同步添加节点不变量、后端发射和独立的行为反例测试；如出现新引用类型，
同时扩展 `SingboxConfigValidator`。固定源码研究证据写入数据表并重新生成文档，
不把证据 ID 放入 IR。

### 来源语义与调优优先级

节点出站的取值顺序为：YAML 显式值 → 精确语义映射 → BoxForge tuning →
sing-box 运行时默认值。BoxForge 不保证逐字段原样复制；它保证已声明的显式
来源语义不会被 tuning 静默覆盖。来源未指定时，BoxForge 可以施加自己的调优。

`ProxyNodeParser` 在协议解释前按 `Schema` 逐项检查 `ClashObject.Properties`。
五个协议的字段声明与解释钩子集中在各自的 `*Protocol` 中；`NodeCatalogBuilder` 仅编排
严格转换，不重复协议选择和字段解释。已知但未支持的连接字段、尚未分类的字段、冲突的别名和无效的
显式值都会形成包含协议、节点名、字段名及原因的错误。schema 同时声明值形状，
避免把列表或对象转换成字符串；嵌套 Reality 和 Shadowsocks 插件对象也逐项检查。
嵌套声明可以附带使用条件；别名沿用同一嵌套 schema 和冲突检测。
VLESS/Trojan 只接受原生 TCP，旧传输选项作为不支持的来源语义明确拒绝。
`name`/`type` 等元数据和明确无连接作用的选项在 schema 中列明，
不会用通配规则吞掉未知字段。

转换只有严格路径：缺少 `type`、未知协议、字段映射失败和保留 tag 冲突等任一
无效节点使整次转换失败，不返回部分配置，不提供跳过节点并告警的入口。
后续协议工作应把字段从 `UnsupportedSemantic` 改为 `Mapped`，同时提供
映射与反例测试，并重新核对锁定来源与目标核心的实际行为。

`OutboundTuningPolicy` 在协议解析之后、目标发射之前，在节点 IR 副本中填入
缺失的 `connect_timeout`、桌面 TCP keepalive 和 Hysteria2 hop/BBR 值。
目标 DTO 不内置产品策略默认值。DNS 超时、容量、optimistic 超时和默认出口由 profile／计划经 emitter 显式写入；
反向映射、接口探测、日志和 TLS 启用等固定选择由相应构建器或 emitter 显式提供。
可省略的目标字段使用 nullable 表示未指定，序列化不会为它们补值；显式 `false` 和 `0` 仍保留。
`SingboxConfig` 中空的 DNS／route／log 对象及空集合初始化仅用于结构，不填充策略。
直接构造 DTO 的调用方须自行提供所需值；需要完整产品配置时使用引擎。
顺序 DNS 主解析器预算由 `DnsPlan.SequentialPrimaryTimeout` 单独定义，规划与计划一致性校验共用，
协议默认语义及目标校验器的独立安全断言仍保留。
连接超时来自平台 profile，路由 sniff 超时来自 `route.sniff_timeout`，默认仍为
`300ms`。当前 YAML 显式
HY2 hop/BBR 值优先于调优。显式固定间隔不会补入调优的随机上限；显式带宽
启用时不会注入 BBR profile（显式 profile 仍保留，核心仅在 BBR 模式使用）。

协议相关职责集中在现有模块，不在 CLI 或 Server 中重复转换：

| 模块 | 职责 |
| --- | --- |
| `ProxyNodeParser` / `*Protocol` / `SourceFieldSchemas` | 单一协议解析入口、来源字段声明、值与组合校验 |
| `TlsConfigHelper` | 共享 TLS、uTLS 与 Reality 映射 |
| `Models/Singbox` | 目标 JSON 形状与已支持的多态类型，不携带来源字段或调优策略 |
| `OutboundTuningPolicy` / profile 策略 | 仅对缺失值施加 BoxForge 调优 |
| `ConfigSerializer` | 序列化已校验模型，仅省略 null 和空集合 |

各协议字段、拒绝组合和输出默认值以[生成配置约定](generated-config.md)为准；
来源/目标核心版本、脚本使用方法和未验证路径以
[协议验证边界](protocol-validation.md)为准。

## 确定性与替换边界

`ProfilePlanner` 的地区 inventory 记录实际 leaf membership，与地区 selector
是否生成分离；服务的 `DefaultRegion` 对应 0/1/2+ leaf 时分别选择主组/leaf/
地区 selector。主组优先使用 `route.main_default_region` 对应的已生成地区组，
否则依次回退到首个地区组、首个 leaf 和 DIRECT；单个偏好地区 leaf 不改变该主组规则。

地址族取自有效目标配置的 `dns.address_family`，完整计划统一保存该策略。
默认三个平台均使用 `DirectDualStack`。DNS、route、控制面 resolver 与 `BoxForgeEngine` 的
`Validate(config, addressFamily)` 均使用同一实例中的策略，validator 不再次推导平台。IPv4-only 除解析
策略和 IPv6 节点字面量外，还验证 early/post-resolve IPv6 reject guards 和首条
无条件 AAAA 空 NOERROR；不能用缩窄到特定网络/域名的规则伪装全局 guard。
`DualStack` 不要求上述 IPv4-only 限制。`Auto` 保留双栈 TUN 捕获，
将 AAAA 空答与两道 IPv6 拒绝改为缺少公网 IPv6 出口地址时才生效的条件规则；
各平台统一使用反转的 `default_interface_address: ["2000::/3"]` 条件，
判定发生在核心运行时，不读取生成机器的网卡状态，也不进行主动可达性探测。
Android 依赖客户端通过 libbox 提供默认物理接口和地址，缺少信息时保守拒绝；
锁定核心源码与生成配置检查不代表所有 Android 客户端或真机网络切换都已验证。
条件 AAAA 规则仅约束 client DNS，`Auto` 不保证控制面 resolver 自动使用 IPv4；
来源节点显式 `ip-version: ipv4` 仍保留独立 resolver 约束。
`DirectDualStack` 在 `Auto` 的条件守卫之上按固定路由类别限制代理侧：不按解析池类别过滤 AAAA；
路由在优先服务、其他服务各自阶段之前拒绝对应规则集的 IPv6，保留国内 UDP/443 的先行直连优先级，
在 `route.final` 前对全部其余流量拒绝 `ip_version: 6`，控制面 resolver 与 rule-set HTTP client 使用 `ipv4_only`。
直连出站仍使用 `prefer_ipv4`。validator 额外要求最后一条路由规则是无条件 IPv6 拒绝，
且每条代理路由之前都有覆盖其规则集的 IPv6 拒绝。
`DualStack` 可显式启用，不按出口地址拒绝 IPv6。Android 不输出未实现的 strict_route；
本地静态验证不等于真机防逃逸抓包或网络切换证明。

- 路由规划器按 `RoutePhase` 枚举顺序展开规则及 Linux 的 bypass/bridge 伴随规则，
  `RoutePlan` 直接检查实际规则的阶段顺序和必要策略，不再另存一份完整阶段列表。
  后端映射有序结果，[路由顺序](routing-and-dns.md#路由规则顺序)由行为测试检查；
  规则上的阶段标记不进入目标 JSON。目标 validator 独立检查实际规则。
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
来源字段、端口和认证材料在协议 Schema 与解释钩子中校验；目标语法与核心默认语义由
自动化的官方 `check` 覆盖，校验器不再维护一份目标核心格式规则。
outbound、endpoint 和 inbound 各自的 tag 必须唯一，outbound 与 endpoint
共享路由目标命名空间，tag 也不得互相重复。
内置校验器不会启动 sing-box 或下载核心。`scripts/verify-generated-configs.py`
在独立临时目录中生成 5 个 fixture × 3 平台 × Tailscale/API 开关两种状态的
60 份配置，交给固定版本和 revision 的官方核心检查；下载模式同时校验固定
归档 SHA-256。普通 CI 和发布前验证使用同一脚本。部署实际配置时的核心检查和
运行时验证仍是独立步骤。

[返回 README](../README.md)
