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
2. 将 Clash 字段校验和 sing-box outbound 创建放在该转换器内。
3. 在 `CoreServiceRegistration.AddBoxForgeCore` 中注册新转换器。
4. 增加有效转换和无效字段的单元测试；如果引入新引用类型，同时扩展
   `SingboxConfigValidator`。

## 确定性与替换边界

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
