# 架构与扩展

BoxForge 将命令行、批处理工作流和纯配置构建分开。这使转换规则可在不启动
完整 CLI 的情况下进行单元测试，也让文件系统失败不会污染核心转换逻辑。

## 处理流程

1. `GenerateCommandParser` 解析 `generate` 子命令、路径和平台。
2. `LocalGenerationWorkflow` 校验路径，并按文件名排序读取顶层 YAML。
3. `ConversionService.Prepare` 解析 YAML，将支持的节点转换为 `NodeCatalog`，
   并根据规范化的 `proxies` 列表计算稳定 `cache_id`。
4. 对每个目标平台，`SingboxConfigBuilder` 组合 inbound、endpoint、outbound、
   DNS、route 和 experimental 配置。
5. `SingboxConfigValidator` 检查 BoxForge 自身约束，`ConfigSerializer` 再生成 JSON。
6. 产物先写入临时目录；所有输入和平台成功后，工作流才替换输出目录。

## 目录职责

| 目录 | 职责 |
| --- | --- |
| `App/` | 调度用例并将结果映射为进程退出码 |
| `Cli/` | 命令行模型与无副作用解析 |
| `Workflows/` | 文件批处理、回滚和输出替换 |
| `Parsers/` | Clash YAML 到输入模型 |
| `Converters/` | 各代理协议节点到 sing-box outbound |
| `Builders/` | 将节点目录和平台组合为完整 sing-box 配置 |
| `Configuration/` | 选项、标签、地区/服务定义和依赖注入 |
| `Models/` | Clash 与 sing-box 数据模型 |
| `Services/` | 转换门面、校验、序列化和缓存身份计算 |
| `Tests/BoxForge.Tests/` | 命令行、配置、构建器、顺序和校验器回归测试 |

## 扩展代理协议

1. 在 `Converters/` 实现 `IProxyConverter`，使 `CanHandle` 只识别目标类型。
2. 将 Clash 字段校验和 sing-box outbound 创建放在该转换器内。
3. 在 `ServiceRegistration.AddBoxForge` 中注册新转换器。
4. 增加有效转换和无效字段的单元测试；如果引入新引用类型，同时扩展
   `SingboxConfigValidator`。

## 确定性与替换边界

- 输入文件按文件名排序，平台顺序固定为 Android、Linux、Windows。
- 同名 `.yaml`/`.yml` 会导致对应配置失败，避免不确定的输出覆盖。
- 内容比较使用完整文本；仅当内容与文件集同时相同时，整批才视为无变更。
- 替换时先将旧输出移到同级备份目录，再移入新输出；第二步失败时恢复备份。
- 临时目录与备份目录的清理失败会记录警告，不会将已生效的新输出误报为失败。

## 校验边界

`SingboxConfigValidator` 检查标签、引用、必填字段、端口和生成器特有约束。
它不会启动 sing-box，也不会下载或验证远程 rule-set。发布流程应对每个最终
`config.json` 另行执行目标版本的 `sing-box check`。

[返回 README](../README.md)
