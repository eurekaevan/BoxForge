# 配置参考

BoxForge 提供两个 Tailscale 运行时设置，并提供一个全平台 sing-box API 开关。
使用分组键时，环境变量中的 `__` 对应配置路径中的 `:`。

## 配置表

| 推荐环境变量 | 兼容键 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `BOXFORGE_Tailscale__Enabled` | `BOXFORGE_TailscaleEnabled` | `false` | 是否在 Linux 和 Windows 生成 Tailscale endpoint |
| `BOXFORGE_Tailscale__AndroidEnabled` | `BOXFORGE_TailscaleAndroidEnabled` | `false` | 是否在 Android 生成 Tailscale endpoint |
| `BOXFORGE_SingboxApi__Enabled` | 无 | `false` | 是否生成仅监听本机的 sing-box API 与 Dashboard |

`Enabled` 和 `AndroidEnabled` 只接受 `true` 或 `false`（不区分大小写）。
无法解析的值会使生成失败。Android 不继承桌面端的 `Enabled`：
即使 `Enabled=true`，只要未显式设置 `AndroidEnabled=true`，Android 产物仍不会
包含 Tailscale endpoint、Tailscale DNS 或对应路由。

`SingboxApi:Enabled` 同样只接受 `true` 或 `false`。启用后，三个目标平台都会生成
`type: api` service，固定监听 `127.0.0.1:9090`。Dashboard 文件由目标机器上的
sing-box 下载到工作目录下的 `dashboard`，下载使用现有
`rule-set-direct` HTTP client。CORS 只允许该端口的 `127.0.0.1` 和
`localhost` origin，且不允许浏览器私网跨域访问。

首版 API 配置不开放监听地址、端口或远程访问，也不生成共享 secret。尽管只监听
loopback，同机进程仍可访问该控制面；不需要 Dashboard、远程控制或 Tailscale
交互管理时应保持关闭。

## 代码固定值

| 内容 | 固定值 |
| --- | --- |
| 主代理组 | `🚀 PROXIES` |
| 直连 outbound | `DIRECT` |
| 非 Android L3 直连 outbound | `bridge-out` |
| Tailscale endpoint 标签 | `tailscale` |
| Tailscale DNS 标签 | `tailscale-dns` |
| Tailscale 状态目录 | `tailscale` |
| `accept_routes` | `true` |
| sing-box API（启用时） | `127.0.0.1:9090` |
| Dashboard 目录（启用时） | `dashboard` |

没有使用值的可选字段（`control_url`、`hostname`、`exit_node` 和
`exit_node_allow_lan_access`）不写入生成的 JSON，也不提供环境变量入口。

## Tailscale 运行说明

在目标平台启用后，生成配置包含一个 Tailscale endpoint。它复用 sing-box 已有的系统
VPN/TUN，不创建第二个系统 VPN 接口。登录状态保存在 `StateDirectory`，
不会写入 `config.json`。

Tailscale 控制平面域名通过 `bootstrap` 解析器建立初始连接。该解析器
固定以 IP 字面量 `223.5.5.5` 直连 AliDNS DoH，TLS `server_name` 为
`dns.alidns.com`；它不调用系统解析器，也不经主代理组，避免明文
DNS 和冷启动循环依赖。

`taildrop_directory` 不提供环境变量入口，始终按目标平台生成：

| 平台 | 生成值 | 运行时含义 |
| --- | --- | --- |
| Android | `Taildrop` | SFA 工作目录下的 `Taildrop`；不直接写入公共 Download 目录 |
| Windows | `$USERPROFILE\Downloads\Taildrop` | sing-box 在运行时展开当前进程账户的 `USERPROFILE` |
| Linux | `$HOME/Downloads/Taildrop` | sing-box 在运行时展开当前进程账户的 `HOME` |

Android 的相对路径以 sing-box 工作目录为基准。Windows 和
Linux 的环境变量属于运行 sing-box 的进程账户；由系统服务运行时，
它们不一定指向桌面登录用户。目标账户必须对展开后的目录具有写权限。

发送和管理文件需使用 sing-box 图形客户端、Dashboard 或 `sing-box api`，
并在 Tailscale 管理端启用文件共享。

[返回 README](../README.md)
