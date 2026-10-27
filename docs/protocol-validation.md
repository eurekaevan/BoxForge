# 协议转换与验证边界

研究基线为 Mihomo Meta/Alpha `88dcbf7f1614a67c3b36b848ee3592dfa92ada36`
及官方 sing-box `1.15.0-alpha.8`、revision
`b609f959f57ce34416c51c7b87ce4a76f2e1df56`。

<!-- BEGIN GENERATED:core-baseline -->
当前最低支持与资格验证核心为官方 sing-box `1.15.0-alpha.12`，
revision `25b0d5ea3e466d246c708ac0f20d0448110e566c`。版本、revision 与归档摘要统一来自
[核心锁定数据](data/sing-box-core.json)；不代表验证了所有后续版本或定制构建。
<!-- END GENERATED:core-baseline -->

## TCP、TLS 与 Reality 支持范围

TLS 指纹仅接受两端等价支持的现代命名 profile 和 `randomized`；`none` 关闭
uTLS。经典版本、历史 Chrome 别名、不同随机分布的 `random` 和未知值拒绝。
Reality 必须有有效 uTLS，VLESS 还必须显式启用 TLS；不擅自补默认指纹。
完整字段约束见[生成配置](generated-config.md#tcptls-与-reality)。

VLESS/Trojan 只支持省略或显式 `tcp` 的 `network`，不再生成 V2Ray
`transport`。WS、gRPC、HTTPUpgrade、HTTP、H2、xHTTP 等传输方式及其
选项均拒绝，不能按原生 TCP 继续转换。保留原生 TCP、TLS、uTLS 和 Reality。
Shadowsocks 保留普通 SS、UoT 和 obfs HTTP/TLS，拒绝 `v2ray-plugin`。

## 可重复的本地验证

从仓库根目录运行 .NET 验证：

```bash
dotnet format BoxForge.slnx --verify-no-changes
dotnet build BoxForge.slnx --no-incremental --warnaserror
dotnet test BoxForge.slnx --no-build
python3 -m unittest discover -s scripts -p 'test_*.py'
```

官方配置检查已有统一入口，默认下载固定的官方 Linux amd64 核心并核验归档
SHA-256、版本及 revision；其他平台或已有核心可用 `--core` 指定路径。
版本查询限时 10 秒、每轮 CLI 生成限时 60 秒、每次核心配置检查限时 30 秒。
反例必须以状态 1 退出，且诊断匹配该用例的字段路径及错误主题；无关错误、
信号终止和超时均判为验证失败，并保留用例上下文及进程输出。
标准库测试使用假进程检查这些失败边界，不代替下述官方核心验证：

```bash
python3 scripts/verify-generated-configs.py src/BoxForge.Cli/bin/Debug/net10.0/BoxForge.dll
# 或复用本地锁定核心
python3 scripts/verify-generated-configs.py src/BoxForge.Cli/bin/Debug/net10.0/BoxForge.dll --core /absolute/path/to/sing-box
```

脚本检查 5 个 fixture × 3 平台 × Tailscale/API 开关组合的 60 份配置；在临时
fixture 副本中追加 HY2 单端口、等端点范围、多标量、混合范围和重复/边界端口样本，
断言目标端口表示并逐份交给官方核心，不修改仓库 fixture 或字节基线。另验证
6 类核心反例会被拒绝。普通 CI 和发布前验证调用同一入口；生产 CLI 和 Server
不下载或启动核心。输入字段合法性仍由解析器和转换器检查，官方 `check`
不保证运行时可用性，例如空服务器地址和零端口并不一定在此阶段报错。
内置校验器保留 DNS 地址族约束、标签与引用完整性、控制面和来源映射等项目约束；
路由策略由高层计划、发射器和产物测试保障，不再从目标路由 AST 逆向重建。

以下可选回环验证使用显式保留的生成目录，供后续脚本读取产物。
再使用 CLI 从 `Tests/BoxForge.Tests/Fixtures` 生成三平台配置，分别覆盖 API
关闭和开启。下面的 Bash 命令使用独立临时目录，不替换正式输出目录；
将 `singbox_core` 改为上述官方核心的绝对路径。

```bash
singbox_core=/absolute/path/to/sing-box
verification_root=$(mktemp -d /tmp/boxforge-verification.XXXXXX)

BOXFORGE_features__api__enabled=false \
BOXFORGE_dns__address_family=Ipv4Only \
dotnet run --no-build --project src/BoxForge.Cli -- generate \
  --input-dir Tests/BoxForge.Tests/Fixtures \
  --output-dir "$verification_root/standard" --platform all || exit

BOXFORGE_features__api__enabled=true \
BOXFORGE_dns__address_family=Ipv4Only \
dotnet run --no-build --project src/BoxForge.Cli -- generate \
  --input-dir Tests/BoxForge.Tests/Fixtures \
  --output-dir "$verification_root/api" --platform all || exit

for config in "$verification_root"/*/*/*/config.json; do
  "$singbox_core" check -c "$config" || exit
done

python3 scripts/verify-dns-races.py "$singbox_core" \
  "$verification_root/standard/profiles/Linux/config.json" || exit

node scripts/verify-shadowsocks.mjs "$singbox_core" \
  "$verification_root/standard/shadowsocks/Linux/config.json" || exit
node scripts/verify-session-protocols.mjs "$singbox_core" \
  "$verification_root/standard/hysteria2/Linux/config.json" hysteria2 || exit
node scripts/verify-session-protocols.mjs "$singbox_core" \
  "$verification_root/standard/anytls/Linux/config.json" anytls || exit
```

上述本地协议验证显式使用 `Ipv4Only`（与内置默认一致），使 AAAA 预期不依赖宿主网卡；这不验证 `Auto` 或 `DirectDualStack` 的实际网络判断。
DNS 验证脚本读取真实生成的桌面 DNS 规则，将上游替换为本地受控 DoH，
将分类替换为同标签 inline 测试域名；不创建 TUN、不使用真实订阅或公网 DNS。
脚本接受 `Ipv4Only` 或 `DualStack`，要求 `ParallelFastest` 和 `dns.timeout = 3s`，
不匹配时会在启动核心前报错。核心版本查询限时 10 秒，配置检查和证书生成各限时 30 秒。
需要 Python 3 和 OpenSSL，本地 DoH 通过显式信任证书使用 HTTP/1.1，不能据此
证明生产 DoH 的 HTTP/2、代理链路或 Gmail 实际性能。它覆盖地址/空答竞争、
CNAME、附加区与错误地址族、非地址查询、分类隔离、GitHub/Spotify/Games
与 `cn` 重叠时的代理 DNS 优先和失败隔离、TTL/过期缓存和默认 3 秒
负答等待；`DualStack` 还验证 AAAA 有效地址优先及其缓存边界。诊断和结果写入独立的 `/tmp/boxforge-dns-races.*` 目录。

协议验证脚本使用真实生成的 Linux leaf，而不是重新手写客户端字段。对应范围：

- `scripts/verify-shadowsocks.mjs`：普通 SS TCP 与 UoT v1/v2 UDP echo；SS 服务端仅监听 TCP，防止原生 UDP 假通过。
- `scripts/verify-session-protocols.mjs`：最后一个参数为 `hysteria2` 或 `anytls`；
  验证 HY2 默认、显式带宽、Gecko、保留 20 秒间隔的跳端口，以及 AnyTLS 会话字段。

脚本只改回环拨号地址/端口和临时证书信任，保留生成的协议字段；临时服务与进程
由脚本清理。HY2 跳端口使用两个临时 UDP 转发端口，观察保留间隔后的 UDP source
socket 轮换并报告目的端口流量；[指定依赖随机选端口](https://github.com/SagerNet/sing-quic/blob/4f371c86a365/hysteria/hop.go)，合法跳转可能选回原目的端口，
因此不要求一次跳转后两个目的端口都被选中。
需要 Node.js、OpenSSL；session 脚本还需要 curl。
脚本核对版本及 revision；fixture 全部使用虚构凭据。

SS obfs HTTP/TLS 目前只有 serializer/官方核心配置检查，
目标 SS inbound 不提供插件服务端，因此脚本明确输出 `CHECK-ONLY`，不是 runtime PASS。
这些验证不包含生产订阅、远程服务器、真实 TUN、防火墙、Android 设备或网络泄漏抓包。

## 配置默认值与验证覆盖

输出层仅省略 `null` 和空集合，保留模型中已有的标量值；不会因其等于锁定
核心默认值而删除。未映射或未调优的可选字段仍可能依赖运行时核心默认值。
回环脚本改写拨号地址时保留原 TLS 名称语义，避免证书名称随测试地址变化。

常规验收包括 `dotnet format`、warnings-as-errors build、全部测试，以及统一
脚本的 60 份 JSON 官方检查和 6 类拒绝反例。
另外核对地区 AUTO 层级、有效默认选择、DNS/route 引用及控制面 resolver。
单元测试覆盖循环和超限 YAML、指纹/Reality 拒绝、非 TCP network 和旧传输选项拒绝；
真实 Kestrel 子进程覆盖环境开关和截断 multipart 的错误契约。

| 场景 | 回环脚本验证内容 |
| --- | --- |
| SS plain TCP、UoT v1/v2 | HTTP/UDP echo，UoT 后端仅接受 TCP |
| SS obfs HTTP/TLS | CHECK-ONLY：无插件服务端，不标为 runtime PASS |
| HY2 默认、100/80 Mbps、Gecko 600/1200、固定 20 秒 hopping | HTTP body 与 hopping 后 UDP source socket 轮换 |
| AnyTLS session fields | 生成字段断言与 HTTP body |

## Linux TUN 多队列对照

[`multi_queue`](https://sing-box.sagernet.org/configuration/inbound/tun/#multi_queue)
仅适用于 Linux 自有栈。BoxForge 的 Linux 输出显式启用该字段，Windows/Android
省略；配置检查已覆盖，真实吞吐收益尚未验证。下面从同一份 Linux 配置生成
仅此字段不同的两个候选，不修改来源文件：

```bash
python3 - /absolute/path/to/Linux/config.json /tmp/boxforge-tun-comparison <<'PY'
import copy
import json
import os
import pathlib
import sys

source = json.loads(pathlib.Path(sys.argv[1]).read_text())
assert sum(item["type"] == "tun" for item in source["inbounds"]) == 1
root = pathlib.Path(sys.argv[2])
root.mkdir(mode=0o700)  # Existing directories are rejected; configs can contain credentials.
for name, enabled in [("single", False), ("multi", True)]:
    candidate = copy.deepcopy(source)
    next(item for item in candidate["inbounds"] if item["type"] == "tun")["multi_queue"] = enabled
    with os.fdopen(os.open(root / f"{name}.json", os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as output:
        json.dump(candidate, output, ensure_ascii=False, indent=2)
        output.write("\n")
PY

"$singbox_core" check -c /tmp/boxforge-tun-comparison/single.json
"$singbox_core" check -c /tmp/boxforge-tun-comparison/multi.json
```

吞吐测试必须使用有真实 TUN 权限的 Linux 环境和可用节点；fixture 的虚构凭据
只能用于配置检查。顺序运行两个候选，固定同一节点、核心、测试服务器和负载，
避免 AUTO 在轮次间选中不同节点；使用相同预热时间，交替重复单流/多流 TCP 和 UDP，
记录吞吐、尾延迟、丢包和 CPU。保留生成配置原有的 `auto_redirect` 等设置；部分
TCP 会由 redirect 路径处理，因此需确认测试流量经过 TUN，不能将其他路径的结果
归因于多队列。官方 `check` 不创建 TUN，也不能替代上述性能对照。

## 已知验证边界

协议脚本仅验证 sing-box 与 sing-box 回环，配置检查不等于实时 DNS、tailnet、
kernel bypass、Windows ZIP 解压、真实 TUN 或设备级 IPv6 防逃逸证明。

未经运行时证明的路径包括 VLESS/Trojan 原生 TCP/TLS、Reality、全部 uTLS 指纹
和真实远程部署；TLS fixture 只提供生成配置与官方检查覆盖。
每次验收报告应注明实际运行的测试、产物和核心版本；本文定义验证方法与边界，
不把历史通过记录视为当前代码或目标设备已经通过。

[返回 README](../README.md)
