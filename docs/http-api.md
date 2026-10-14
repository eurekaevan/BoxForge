# HTTP API

`BoxForge.Server` 是 `IBoxForgeEngine` 的无状态 HTTP 适配器。它接收
Clash YAML，在内存中生成 sing-box 配置，不读写 CLI 的输入或
输出目录。

## 启动

```bash
dotnet run --project src/BoxForge.Server --urls http://127.0.0.1:5080
```

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/` | 同源 YAML 上传与 ZIP 下载页面 |
| `GET` | `/healthz` | 进程健康检查 |
| `POST` | `/api/v1/convert` | 以 JSON 返回原始配置字符串和 SHA-256 |
| `POST` | `/api/v1/export` | 上传 YAML 并下载 ZIP |

Server 默认不启用 CORS，页面也只调用同源端点。

## 健康检查

```bash
curl http://127.0.0.1:5080/healthz
```

```json
{
  "status": "ok"
}
```

## JSON 转换

`POST /api/v1/convert` 要求 `Content-Type: application/json`，且不接受
契约外字段。

```json
{
  "name": "example",
  "yaml": "proxies:\n  - name: test\n    type: ss\n    server: node.example.com\n    port: 443\n    cipher: aes-128-gcm\n    password: test-only",
  "platforms": ["Android", "Linux", "Windows"]
}
```

- `name` 必须为 1～100 个 Unicode 标量值，UTF-8 编码最多 255 字节。
  为保证 ZIP 可以跨平台解压，名称不能是 `.` 或 `..`，不能包含
  `< > : " / \ | ? *` 或控制字符，也不能以空格或 `.` 结尾。
  Windows 设备保留名（包括带扩展名的 `CON`、`PRN`、`AUX`、`NUL`、
  `COM1`～`COM9`、`LPT1`～`LPT9`，以及上标数字变体和 `CONIN$`、
  `CONOUT$`）均被拒绝，不区分大小写。
- `yaml` 必须非空，UTF-8 大小不得超过 2 MiB。
- `platforms` 至少包含一项，只允许 `Android`、`Linux` 和
  `Windows`，不区分大小写且不得重复；数字和逗号组合名称不被接受。

成功响应为 `application/json`：

```json
{
  "name": "example",
  "artifacts": [
    {
      "platform": "Android",
      "path": "example/Android/config.json",
      "sha256": "<小写十六进制 SHA-256>",
      "content": "{\n  ...\n}"
    }
  ]
}
```

`content` 是引擎生成的原始字符串；`sha256` 对该字符串的
UTF-8 字节计算。产物固定按 Android、Linux、Windows 排序。

## YAML 上传与 ZIP 导出

`POST /api/v1/export` 要求 `Content-Type: multipart/form-data`。

| 字段 | 要求 |
| --- | --- |
| `file` | 必须且只能有一个 `.yaml` 或 `.yml` 文件；不得为空，最大 2 MiB |
| `name` | 可选；缺省时使用去掉扩展名的上传文件名 |
| `platforms` | 必须至少重复提交一次；值的规则与 JSON 端点相同 |

`name`（含缺省时的文件名主干）遵循 JSON 端点相同的跨平台名称限制。
文件必须使用有效 UTF-8，可以包含开头的 UTF-8 BOM。
扩展名只用于初步约束，不依赖文件 part 声明的 `Content-Type` 判断内容。
文件内容仍必须通过 BoxForge 的严格 YAML 解析和转换，包括
[循环引用与资源限制](architecture.md#不可信-yaml-的资源边界)。其他表单字段会被拒绝。

```bash
curl --request POST http://127.0.0.1:5080/api/v1/export \
  --form 'file=@clashConfigs/example.yaml' \
  --form 'name=example' \
  --form 'platforms=Android' \
  --form 'platforms=Linux' \
  --form 'platforms=Windows' \
  --output boxforge-output.zip
```

无论选择一个还是多个平台，成功响应均为：

```http
Content-Type: application/zip
Content-Disposition: attachment; filename="boxforge-output.zip"
Cache-Control: no-store
```

```text
{name}/
├── Android/config.json
├── Linux/config.json
└── Windows/config.json
```

只生成所请求的平台目录，entry 顺序仍固定为 Android、Linux、
Windows。每个 `config.json` 与引擎产物的 UTF-8 字节完全一致。
引擎完成全部平台后才会构建 ZIP，任一平台失败时不返回部分
压缩包。

## 限制与错误

整个 HTTP 请求体不得超过 4 MiB。错误使用
`application/problem+json`：

| 状态码 | 含义 |
| --- | --- |
| `400` | JSON、表单、名称、文件或平台参数无效 |
| `413` | 请求体超过 4 MiB，或 YAML 超过 2 MiB |
| `422` | YAML 已接收，但无法解析或转换 |
| `500` | 未预期的服务器错误 |

错误响应不包含 YAML、节点凭据、完整生成配置或异常堆栈。
截断的 multipart（例如缺少结束边界）按无效请求返回 `400`，不作为内部错误；
请求取消不转成输入错误，真正的服务器 I/O 故障仍返回 `500`。

Server 读取与 CLI 相同的 `BOXFORGE_` 环境配置，包括 Tailscale 和可选 sing-box API
开关；这是生成配置的开关，不改变 BoxForge HTTP Server 的监听地址。
变量和默认值见[配置参考](configuration.md#配置表)。

## 状态与安全边界

- Server 不将上传、请求或转换结果写入磁盘；multipart 内存
  缓冲阈值与 4 MiB 请求上限一致。
- Server 不接收订阅 URL，不进行远程 fetch，也不保存账号或转换历史。
  生成配置中的 rule-set 和 Dashboard 下载由运行该配置的 sing-box 执行，
  不属于 BoxForge Server 请求处理过程。
- 上传内容、节点名和转换错误细节不写入 Server 日志。
- 响应设置 `Cache-Control: no-store`、CSP、`X-Content-Type-Options:
  nosniff` 和 `Referrer-Policy: no-referrer`。
- 当前没有鉴权、限流和完整的公网部署防护，不应直接暴露到
  公网。

[返回 README](../README.md)
