# 版本与发布契约

BoxForge 的 Core、CLI 和 Server 共用配置生成契约。CLI 保留 `generate`，
HTTP/Web 保留无状态输入到结果的边界；本阶段不改变配置生成策略，也不引入
订阅抓取、账户、持久化、定时任务或配置托管。

## 日期版本

版本与 Git tag 使用发布当天在 `Asia/Taipei` 的日期，不加 `v`：
`YYYY-MM-DD`；同日第二次发布为 `YYYY-MM-DD.2`，之后递增。
日期只表示发布顺序，不表示兼容程度。已发布的 tag、包和说明不可覆盖或移动；
修复应使用新版本。不要用字典序决定同日版本先后（`.10` 晚于 `.9`）。

每个 tag 必须有同名的 `docs/release-notes/<tag>.md`，并对应固定源码提交。
仓库中的日期说明是发布候选材料；GitHub Release 实际发布后才代表可下载版本。

## 当前契约基线

| 契约 | 当前承诺 | 详细依据 |
| --- | --- | --- |
| Input | 读取 Clash/Mihomo YAML 的 `proxies`；支持 Trojan、VLESS、Hysteria2、Shadowsocks、AnyTLS 的已声明子集。任一无法安全映射的节点使引擎请求失败；不转换来源 rules/proxy-groups | [来源字段与 strict conversion](architecture.md#来源语义与调优优先级)、[字段与拒绝项](generated-config.md) |
| Target | 生成面向 sing-box 1.15 的配置；因 Tailscale `on_demand` 最低要求 alpha.1。当前资格验证基线是官方 alpha.8 / `b609f959f57ce34416c51c7b87ce4a76f2e1df56`，不代表验证了所有后续版本或定制构建 | [验证边界](protocol-validation.md) |
| CLI | `generate`；既有参数、退出码、三平台目录结构与整批事务替换语义保持稳定 | [CLI 契约](../README.md#输出与事务语义) |
| Behavior | IPv4-only、DNS/route 顺序、地区 selector/URLTest、平台 TUN/bridge、默认 Tailscale 与缺失字段 tuning 均是 BoxForge 策略 | [生成配置](generated-config.md)、[DNS 与路由](routing-and-dns.md)、[开关与默认值](configuration.md) |

每次发布说明都必须逐项记录 Input、Target、CLI、Behavior；未变化也明确写出。
策略变化应说明受影响的平台、触发条件、前后行为，以及同一 YAML 是否会产生
不同配置。首次发布记录基线，不声称与不存在的旧版本兼容。

固定 BoxForge 版本只消除工具源码漂移。复现生成文件还需要固定输入内容、目标
平台、`BOXFORGE_` 开关和运行目录中的宿主配置（CLI 使用 .NET 默认配置加载）。
不要把含凭据的 YAML、配置或环境变量值写入公共发布说明或日志。
远程 rule-set、网络环境和 sing-box 本身影响运行行为，文件一致不等于长期网络行为一致。

## 发布流程

1. 在 `docs/release-notes/<tag>.md` 写完整的四类契约、验证范围和升级注意事项。
2. 按[协议验证边界](protocol-validation.md)完成适用于本次变更的官方核心检查；
   涉及协议时执行对应回环验证，记录结果，不把单元测试当作设备或真实网络证明。
3. 提交变更后，可手动运行 Release workflow，输入候选 tag；只构建验证并保存
   Actions artifact，不创建 tag 或 GitHub Release。
4. 确认后在该提交创建并推送日期 tag。tag 触发完整测试、自包含打包、解包后
   三平台 fixture 生成与源码产物比对，通过后创建 GitHub Release。
5. 发布成功后再更新 BoxVault 的固定版本与 SHA，并检查真实生成流程。

自动发布门禁覆盖 .NET 测试与包的真实生成行为，不自动下载官方 sing-box，
不替代第 2 步的资格验证。首次交付仅提供 Linux x64（glibc，CI 使用 Ubuntu
24.04），不包含 Server，也不声称支持 Alpine/musl 或其他主机架构。
包内包含 .NET runtime，无需安装 SDK；系统原生依赖仍遵循
[.NET Linux 部署要求](https://learn.microsoft.com/en-us/dotnet/core/install/linux)。
采用常规 self-contained 文件夹打包，不使用 NativeAOT、裁剪或单文件自解压。

发布附件：

- `boxforge-<tag>-linux-x64.tar.gz`：含 `boxforge` 启动文件、依赖、README、docs 和 `RELEASE.json`。
- `SHA256SUMS`：归档校验和，发现损坏或与记录不符；与包同源的校验和不构成独立签名。
- `RELEASE.json`：tag、完整 commit SHA、构建 SDK 和 RID，用于追溯，不记录输入或环境凭据。

## 消费固定版本

初期可继续源码运行：将 BoxVault 的 BoxForge checkout `ref: main` 换为已发布
tag，记录对应完整 SHA；更严格时直接以该 SHA checkout，并在注释中标注 tag。
不要提前引用尚不存在的 tag，也不要自动追随 `latest`。

消费二进制时，先在 Release 页面选择一个已发布 tag，并核对 `RELEASE.json`
中的 commit。以下变量必须替换为该真实版本；在独立临时目录运行：

```bash
boxforge_tag=YYYY-MM-DD
boxforge_commit=FULL_COMMIT_SHA
gh release download "$boxforge_tag" --repo eurekaevan/BoxForge \
  --pattern "boxforge-$boxforge_tag-linux-x64.tar.gz" \
  --pattern SHA256SUMS --pattern RELEASE.json
sha256sum --check SHA256SUMS || exit
python3 - "$boxforge_tag" "$boxforge_commit" <<'PY'
import json, sys
with open('RELEASE.json') as source:
    manifest = json.load(source)
assert manifest['tag'] == sys.argv[1], 'Unexpected release tag'
assert manifest['commit'] == sys.argv[2], 'Unexpected source commit'
assert manifest['rid'] == 'linux-x64', 'Unexpected runtime'
PY
if [ "$?" -ne 0 ]; then exit 1; fi
mkdir boxforge
tar -xzf "boxforge-$boxforge_tag-linux-x64.tar.gz" -C boxforge
./boxforge/boxforge generate --input-dir /path/to/clashConfigs \
  --output-dir /path/to/singboxConfigs --platform all
```

升级时先读四类变化，在独立输出目录对比旧版与新版生成结果，再用相应官方
核心检查新产物。确认之后更新消费端版本；不要直接用正式输出目录做升级试验。
