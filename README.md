# BoxForge

BoxForge 从 Clash/Mihomo 节点定义生成适用于 Android、Linux 和 Windows 的
sing-box 配置，内置明确的 DNS、路由与平台策略。

BoxForge 不以完整复刻 Clash 配置为目标。它严格保留受支持且可安全映射的
节点语义，并按 BoxForge 的 DNS、路由、平台和调优策略生成完整配置。
输入使用 YAML 的 `proxies` 列表；来源的 `rules`、`proxy-groups` 等顶层
配置不会被转换。支持范围与拒绝项见[协议验证边界](docs/protocol-validation.md)。

CLI 会为 Windows、Android 和 Linux 批量生成平台化配置，并在
整批成功后一次性替换输出目录；Server 则提供无状态的内存转换 API。
两者共用同一个引擎与策略契约，分别服务自动化和 HTTP/Web 使用场景。

## 特性

- 支持 `trojan`、`vless`、`hysteria2`、`shadowsocks` (`ss`) 和 `anytls`；
  VLESS/Trojan 仅接受原生 TCP，可使用受支持的 TLS、uTLS 与 Reality
- 自动生成地区 URLTest AUTO、人工 selector、DNS、路由规则和远程 rule-set
- 全局 DNS 与域名解析只使用 IPv4，所有 AAAA 查询返回空 `NOERROR`
- TUN 保留仅用于捕获的 IPv6 地址，并在 sniff 前拒绝全部 IPv6；解析后另有兜底拒绝，防止硬编码地址、缓存和内置 DoH 逃逸
- Linux 为 sniff 前快路径及 sniff 后 UDP 国内直连提供 `bridge` L3
  forwarding，并优先使用 `auto_redirect` kernel-level `bypass`；Windows 使用普通 DIRECT
- 默认生成 sing-box 内置 Tailscale endpoint，支持按需连接、MagicDNS、子网路由和 Taildrop
- 可选仅监听本机的 sing-box API 与 Dashboard，默认不生成
- 提供不依赖文件系统的 `IBoxForgeEngine` 内存转换边界
- 每个 YAML 只解析和转换节点一次，再复用于所有目标平台
- 输入与平台按固定顺序处理，生成结果具有确定性
- 生成内容未变时跳过；任意项失败时回滚整批输出
- 稳定的退出码，可直接用于 CI 或其他自动化脚本
- 提供最小化 ASP.NET Core HTTP API，请求和结果均不落盘

## 快速开始

Release workflow 自动使用台北时区的日期 tag（`YYYY-MM-DD`，同日补发为 `.2`、`.3` 等）。
发布说明默认留空，可选 GitHub 原生自动生成；无需在仓库提交每个 tag 的更新文档。
发布流程提供 Linux x64 自包含 CLI 包、SHA-256 校验和及源码提交信息；
下载安装、固定源码版本与升级检查见[版本与发布契约](docs/releases.md)。
以下为源码运行方式。

需要 .NET SDK 10.0。生成配置面向 sing-box 1.15 系列；当前最低要求为
`1.15.0-alpha.12`，统一覆盖 Tailscale endpoint 的 `on_demand` 和 Linux
TUN 的 `multi_queue`；不承诺更早 alpha 版本兼容。

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

如果当前目录是 `src/BoxForge.Cli`，可直接使用
`dotnet run -- generate`。

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `--input-dir` | `clashConfigs` | 读取目录顶层的 `.yaml` 和 `.yml` |
| `--output-dir` | `singboxConfigs` | 成功后替换的输出目录 |
| `--platform` | `all` | `Android`、`Linux`、`Windows` 或 `all`，不区分大小写 |

BoxForge 支持 `generate` 和 `profile show --resolved`。后者可通过 `--profile`
选择覆盖文件并输出最终快照。缺少子命令、传入未知选项或重复选项时，程序会输出
用法并立即结束，不会读取 stdin。

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

API 接收配置名、Clash YAML 原文、目标平台和可选的内联 profile，不读写本地配置
目录，不接收订阅 URL，也不保存请求或结果。当前没有鉴权、限流
和完整的公网部署防护，因此不应直接暴露到公网。

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
替换通过先移走旧目录、再移入新目录完成，两步之间输出路径可能短暂不存在，
不保证并发读取者看到无间断的原子切换。
任意一项失败时，旧输出保持不变，已写入临时目录的变更在摘要中计为
“已回滚”。

| 退出码 | 含义 |
| --- | --- |
| `0` | 全部生成成功，或所有内容未变而跳过 |
| `1` | 启动配置无效，或存在转换、校验、读取、写入失败 |
| `2` | 命令行参数无效 |
| `130` | 任务被取消 |

## 运行时配置

统一 profile 支持运行时开关、全局策略、平台参数，以及地区、服务、rule-set 来源和
resolver。CLI 使用 `generate --profile profile.json`，Server 可在启动时加载同一文件，
HTTP 请求也可提交内联 `profile`。优先级从低到高为：内置默认值 → profile 文件 →
宿主环境配置 → 请求内联 profile；请求覆盖仅作用于本次转换。
`profile show --resolved --profile profile.json` 输出最终有效快照。格式与合并规则见
[统一 profile](docs/configuration.md#统一-profile)，请求格式见 [HTTP API](docs/http-api.md)。

以下 `BOXFORGE_` 环境开关同时适用于 CLI 和 HTTP Server；它们控制生成的
sing-box 配置，不改变 BoxForge Server 自身的监听地址。

Tailscale endpoint 默认在三个目标平台生成，并启用 `on_demand`。如需关闭
Linux 和 Windows 的 Tailscale：

```bash
BOXFORGE_Tailscale__Enabled=false \
dotnet run --project src/BoxForge.Cli -- generate --platform Linux
```

Android 使用独立开关；如需关闭，设置
`BOXFORGE_Tailscale__AndroidEnabled=false`。两个开关互不继承，便于只在指定
平台关闭 endpoint。

sing-box API 默认关闭。需要在生成配置中加入本机 API 与 Dashboard 时设置：

```bash
BOXFORGE_SingboxApi__Enabled=true \
dotnet run --project src/BoxForge.Cli -- generate
```

生成的 API 固定监听 `127.0.0.1:9090`，不接受远程监听配置。

TUN 地址、探测、连接和 DNS 参数可通过 profile 调整；字段与默认值见
[配置参考](docs/configuration.md)。

## 文档

- [版本与发布契约](docs/releases.md)：日期版本、兼容性与策略基线、发布和升级步骤
- [架构与扩展](docs/architecture.md)：处理流程、目录职责、整批替换与失败回滚和校验边界
- [HTTP API](docs/http-api.md)：JSON 转换、YAML/ZIP 导出、错误契约和安全边界
- [配置参考](docs/configuration.md)：所有环境变量、默认值与 Tailscale 行为
- [生成配置约定](docs/generated-config.md)：平台差异、缓存、节点和 rule-set 行为
- [协议映射证据](docs/protocol-mappings.md)：数据表生成的来源/目标解释与固定源码证据
- [DNS 与路由优先级](docs/routing-and-dns.md)：实际生成顺序和设计意图
- [协议验证边界](docs/protocol-validation.md)：锁定核心、支持限制和可重复验证

## 开发与验证

```bash
dotnet format BoxForge.slnx --verify-no-changes
dotnet build BoxForge.slnx --no-incremental --warnaserror
dotnet test BoxForge.slnx --no-build
node --test scripts/verification-process.test.mjs
python3 scripts/generate-protocol-mappings.py --check
python3 scripts/verify-generated-configs.py src/BoxForge.Cli/bin/Debug/net10.0/BoxForge.dll
```

内置校验器只检查 BoxForge 的生成约束，包括 IPv6 守卫、标签与引用完整性、
控制面策略和来源映射保真；输入字段合法性由解析器和转换器检查。
官方检查脚本默认下载并验证锁定核心，覆盖 60 份三平台 fixture 配置与核心反例，
同时接入普通 CI 和发布前验证；已有核心可通过 `--core /path/to/sing-box` 指定。
它不创建 TUN 或验证连通性；部署实际配置前仍可执行 `sing-box check -c <config.json>`。

当前资格验证锁定官方 `1.15.0-alpha.12`，并将单元测试、官方配置检查、
本地协议回环与真实设备/网络验证分开记录。完整命令和未验证路径见
[协议验证边界](docs/protocol-validation.md)。
