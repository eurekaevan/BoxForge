# 配置参考

BoxForge 通过 .NET 环境变量配置提供程序读取 `BOXFORGE_` 前缀的运行时设置。
推荐使用分组键，环境变量中的 `__` 对应配置路径中的 `:`。为了兼容旧
部署，所有设置仍接受扁平键；两种写法同时存在时，分组键优先。

## 配置表

| 推荐环境变量 | 兼容键 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `BOXFORGE_Singbox__MainProxyGroup` | `BOXFORGE_MainProxyGroup` | `🚀 PROXIES` | 主选择器标签，也是默认最终出站 |
| `BOXFORGE_Singbox__Direct` | `BOXFORGE_Direct` | `DIRECT` | 直连 outbound 标签 |
| `BOXFORGE_Tailscale__Enabled` | `BOXFORGE_TailscaleEnabled` | `false` | 是否生成 Tailscale endpoint |
| `BOXFORGE_Tailscale__Tag` | `BOXFORGE_TailscaleTag` | `tailscale` | Tailscale endpoint 标签 |
| `BOXFORGE_Tailscale__DnsTag` | `BOXFORGE_TailscaleDnsTag` | `tailscale-dns` | MagicDNS 服务器标签 |
| `BOXFORGE_Tailscale__StateDirectory` | `BOXFORGE_TailscaleStateDirectory` | `tailscale` | sing-box 保存 Tailscale 登录状态的目录 |
| `BOXFORGE_Tailscale__ControlUrl` | `BOXFORGE_TailscaleControlUrl` | 空 | 可选的 HTTP(S) 控制平面地址 |
| `BOXFORGE_Tailscale__Hostname` | `BOXFORGE_TailscaleHostname` | 空 | 可选的 tailnet 设备名 |
| `BOXFORGE_Tailscale__AcceptRoutes` | `BOXFORGE_TailscaleAcceptRoutes` | `true` | 是否接受 tailnet 通告的子网路由 |
| `BOXFORGE_Tailscale__ExitNode` | `BOXFORGE_TailscaleExitNode` | 空 | 可选的出口节点 |
| `BOXFORGE_Tailscale__ExitNodeAllowLanAccess` | `BOXFORGE_TailscaleExitNodeAllowLanAccess` | `false` | 使用出口节点时是否保留本地网访问 |
| `BOXFORGE_Tailscale__TaildropDirectory` | `BOXFORGE_TailscaleTaildropDirectory` | `Taildrop` | Taildrop 接收目录 |

`Enabled`、`AcceptRoutes` 和 `ExitNodeAllowLanAccess` 只接受 `true` 或 `false`（不区分
大小写）。无法解析的布尔值会使生成失败，而不会被静默当作默认值。

## 校验约束

- `MainProxyGroup` 和 `Direct` 不能为空，也不能相同。
- 启用 Tailscale 时，`Tag` 和 `DnsTag` 不能为空。
- Tailscale `Tag` 不能与 `DnsTag`、`MainProxyGroup` 或 `Direct` 相同。
- 非空 `ControlUrl` 必须是绝对 HTTP(S) URL。
- 没有配置 `ExitNode` 时，`ExitNodeAllowLanAccess` 不会写入生成的 JSON。

## Tailscale 运行说明

启用后，生成配置包含一个 Tailscale endpoint。它复用 sing-box 已有的系统
VPN/TUN，不创建第二个系统 VPN 接口。登录状态保存在 `StateDirectory`，
不会写入 `config.json`。

`TaildropDirectory` 的相对路径以 sing-box 工作目录为基准。发送和管理文件需使用
sing-box 图形客户端、Dashboard 或 `sing-box api`，并在 Tailscale 管理端启用
文件共享。

[返回 README](../README.md)
