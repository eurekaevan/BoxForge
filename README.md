# BoxForge

BoxForge 是一个无交互命令行工具，用于将 Clash YAML 批量转换为
sing-box 1.14 `config.json`。它会为 Windows、Android 和 Linux 生成平台化
配置，并在整批成功后一次性替换输出目录。

## 特性

- 支持 `trojan`、`vless`、`hysteria2`、`shadowsocks` (`ss`) 和 `anytls`
- 自动生成地区分组、服务分组、DNS、路由规则和远程 rule-set
- 可选 sing-box 内置 Tailscale endpoint，支持 MagicDNS、子网路由和 Taildrop
- 每个 YAML 只解析和转换节点一次，再复用于所有目标平台
- 输入与平台按固定顺序处理，生成结果具有确定性
- 生成内容未变时跳过；任意项失败时回滚整批输出
- 稳定的退出码，可直接用于 CI 或其他自动化脚本

## 快速开始

需要 .NET SDK 10.0。生成配置面向 sing-box 1.14；使用 Tailscale endpoint
时需要 sing-box 1.14.0-beta.15 或更高版本。

```bash
dotnet run -- generate \
  --input-dir clashConfigs \
  --output-dir singboxConfigs \
  --platform all
```

三个选项都有默认值，因此也可以直接运行：

```bash
dotnet run -- generate
```

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `--input-dir` | `clashConfigs` | 读取目录顶层的 `.yaml` 和 `.yml` |
| `--output-dir` | `singboxConfigs` | 成功后替换的输出目录 |
| `--platform` | `all` | `Android`、`Linux`、`Windows` 或 `all`，不区分大小写 |

BoxForge 只接受 `generate` 子命令。缺少子命令、传入未知选项或重复
选项时，程序会输出用法并立即结束，不会读取 stdin。

## 输出与事务语义

`--platform all` 会为每个输入生成以下结构：

```text
singboxConfigs/
└── {配置名}/
    ├── Android/config.json
    ├── Linux/config.json
    └── Windows/config.json
```

生成过程先写入输出目录同级的临时目录。全部转换、内置校验和写入
成功后，才用完整的新目录替换旧输出；已不再生成的旧文件也会被移除。
任意一项失败时，旧输出保持不变，已写入临时目录的变更在摘要中计为
“已回滚”。

| 退出码 | 含义 |
| --- | --- |
| `0` | 全部生成成功，或所有内容未变而跳过 |
| `1` | 存在转换、校验、读取或写入失败 |
| `2` | 命令行参数无效 |
| `130` | 任务被取消 |

## 运行时配置

运行时设置通过 `BOXFORGE_` 前缀的环境变量传入。推荐使用分组键，
嵌套层级用双下划线表示：

```bash
BOXFORGE_Singbox__MainProxyGroup='PROXY' \
BOXFORGE_Tailscale__Enabled=true \
BOXFORGE_Tailscale__TaildropDirectory='Received' \
dotnet run -- generate --platform Android
```

完整的配置表、默认值、旧键兼容关系和 Tailscale 约束见
[配置参考](docs/configuration.md)。

## 文档

- [架构与扩展](docs/architecture.md)：处理流程、目录职责、原子替换和校验边界
- [配置参考](docs/configuration.md)：所有环境变量、默认值与 Tailscale 行为
- [生成配置约定](docs/generated-config.md)：平台差异、缓存、节点和 rule-set 行为
- [DNS 与路由优先级](docs/routing-and-dns.md)：实际生成顺序和设计意图

## 开发与验证

```bash
dotnet format BoxForge.slnx --verify-no-changes
dotnet build BoxForge.slnx --warnaserror
dotnet test BoxForge.slnx --no-build
```

内置校验器负责 BoxForge 自身的生成约束，例如标签唯一性、引用完整性和
必填字段。它不代替目标 sing-box 版本的官方检查；部署前应对产物另行执行
`sing-box check -c <config.json>`。
