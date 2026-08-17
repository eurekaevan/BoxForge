# 配置参考

BoxForge 只保留一个运行时设置：是否生成 Tailscale endpoint。使用分组键时，
环境变量中的 `__` 对应配置路径中的 `:`。

## 配置表

| 推荐环境变量 | 兼容键 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `BOXFORGE_Tailscale__Enabled` | `BOXFORGE_TailscaleEnabled` | `false` | 是否生成 Tailscale endpoint |

`Enabled` 只接受 `true` 或 `false`（不区分大小写）。无法解析的值会使生成失败。

## 代码固定值

| 内容 | 固定值 |
| --- | --- |
| 主代理组 | `🚀 PROXIES` |
| 直连 outbound | `DIRECT` |
| Tailscale endpoint 标签 | `tailscale` |
| Tailscale DNS 标签 | `tailscale-dns` |
| Tailscale 状态目录 | `tailscale` |
| `accept_routes` | `true` |

没有使用值的可选字段（`control_url`、`hostname`、`exit_node` 和
`exit_node_allow_lan_access`）不写入生成的 JSON，也不提供环境变量入口。

## Tailscale 运行说明

启用后，生成配置包含一个 Tailscale endpoint。它复用 sing-box 已有的系统
VPN/TUN，不创建第二个系统 VPN 接口。登录状态保存在 `StateDirectory`，
不会写入 `config.json`。

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
