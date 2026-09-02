# BoxForge

BoxForge 用于将 Clash YAML 转换为 sing-box 1.14 `config.json`。
CLI 会为 Windows、Android 和 Linux 批量生成平台化配置，并在
整批成功后一次性替换输出目录；Server 则提供无状态的内存转换 API。

## 特性

- 支持 `trojan`、`vless`、`hysteria2`、`shadowsocks` (`ss`) 和 `anytls`
- 自动生成地区分组、服务分组、DNS、路由规则和远程 rule-set
- 强制代理节点与代理业务使用 IPv4，仅允许命中 `geoip-cn` 的公网 IPv6 直连
- 可选 sing-box 内置 Tailscale endpoint，支持 MagicDNS、子网路由和 Taildrop
- 提供不依赖文件系统的 `IBoxForgeEngine` 内存转换边界
- 每个 YAML 只解析和转换节点一次，再复用于所有目标平台
- 输入与平台按固定顺序处理，生成结果具有确定性
- 生成内容未变时跳过；任意项失败时回滚整批输出
- 稳定的退出码，可直接用于 CI 或其他自动化脚本
- 提供最小化 ASP.NET Core HTTP API，请求和结果均不落盘

## 快速开始

需要 .NET SDK 10.0。生成配置面向 sing-box 1.14；使用 Tailscale endpoint
时需要 sing-box 1.14.0-beta.15 或更高版本。

```bash
dotnet run --project src/BoxForge.Cli -- generate \
  --input-dir clashConfigs \
  --output-dir singboxConfigs \
  --platform all
```

三个选项都有默认值，因此也可以直接运行：

```bash
dotnet run --project src/BoxForge.Cli -- generate
```

如果当前目录是 `src/BoxForge.Cli`，仍可直接使用
`dotnet run -- generate`。`generate` 及其所有参数、默认值和退出码保持不变。

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `--input-dir` | `clashConfigs` | 读取目录顶层的 `.yaml` 和 `.yml` |
| `--output-dir` | `singboxConfigs` | 成功后替换的输出目录 |
| `--platform` | `all` | `Android`、`Linux`、`Windows` 或 `all`，不区分大小写 |

BoxForge 只接受 `generate` 子命令。缺少子命令、传入未知选项或重复
选项时，程序会输出用法并立即结束，不会读取 stdin。

## 无状态 HTTP API

启动 Server：

```bash
dotnet run --project src/BoxForge.Server --urls http://127.0.0.1:5080
```

健康检查：

```bash
curl http://127.0.0.1:5080/healthz
```

将请求保存为 `request.json`：

```json
{
  "name": "example",
  "yaml": "proxies:\n  - name: test\n    type: ss\n    server: node.example.com\n    port: 443\n    cipher: aes-128-gcm\n    password: test-only",
  "platforms": ["Android", "Linux", "Windows"]
}
```

然后调用转换端点：

```bash
curl --request POST http://127.0.0.1:5080/api/v1/convert \
  --header 'Content-Type: application/json' \
  --data-binary @request.json
```

API 只接收配置名、Clash YAML 原文和目标平台，不读写本地配置
目录，不接收订阅 URL，也不保存请求或结果。当前没有鉴权、限流
和 SSRF 防护，因此尚不应直接暴露到公网。

打开 `http://127.0.0.1:5080/` 可使用 Server 自带的上传页面。页面会将
单个 `.yaml` 或 `.yml` 文件发送到同源的
`POST /api/v1/export`，并下载内存中生成的
`boxforge-output.zip`。也可直接调用该端点：

```bash
curl --request POST http://127.0.0.1:5080/api/v1/export \
  --form 'file=@clashConfigs/example.yaml' \
  --form 'name=example' \
  --form 'platforms=Android' \
  --form 'platforms=Linux' \
  --form 'platforms=Windows' \
  --output boxforge-output.zip
```

无论选择一个还是多个平台，`/api/v1/export` 都返回 ZIP。
上传文件上限为 2 MiB，整个请求体上限为 4 MiB。

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

Tailscale endpoint 默认不生成。Linux 和 Windows 需要启用时设置：

```bash
BOXFORGE_Tailscale__Enabled=true \
dotnet run --project src/BoxForge.Cli -- generate --platform Linux
```

即使开启上述通用开关，Android 配置仍默认关闭 Tailscale。需要在
Android 端使用时，单独设置
`BOXFORGE_Tailscale__AndroidEnabled=true`。

其余标签、目录及 endpoint 字段均由代码固定或按目标平台生成，见
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
