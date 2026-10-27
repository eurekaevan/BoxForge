# 版本与发布契约

BoxForge 的 Core、CLI 和 Server 共用配置生成契约。CLI 提供本地批量生成，
HTTP/Web 提供无状态的内存转换；不包含订阅抓取、账户、持久化、定时任务或配置托管。

## 日期版本

手动运行 Release workflow 时，自动按 `Asia/Taipei` 当前时间计算版本与 Git tag，不加 `v`：
`YYYY-MM-DD`；同日第二次发布为 `YYYY-MM-DD.2`，之后递增。
工作流根据已有 tag 的最大同日序号递增，并串行执行发布流程以减少竞争。
日期只表示发布顺序，不表示兼容程度。已发布的 tag 和包不可覆盖或移动；
修复应使用新版本。不要用字典序决定同日版本先后（`.10` 晚于 `.9`）。

每个 tag 对应固定源码提交，GitHub Release 实际发布后才代表可下载版本。
发布说明不要求单独提交仓库文件，默认留空；手动运行时可勾选
`generate_notes`，使用 GitHub 原生生成的 PR 摘要和比较链接，也可在 Release 页面补充。

## 当前契约基线

| 契约 | 当前承诺 | 详细依据 |
| --- | --- | --- |
| Input | 读取 Clash/Mihomo YAML 的 `proxies`；支持 Trojan、VLESS、Hysteria2、Shadowsocks、AnyTLS 的已声明子集。任一无法安全映射的节点使引擎请求失败；不转换来源 rules/proxy-groups | [来源字段与 strict conversion](architecture.md#来源语义与调优优先级)、[字段与拒绝项](generated-config.md) |
| Target | 最低支持与资格验证版本见下方自动生成的核心门禁；覆盖 Tailscale `on_demand` 与 Linux TUN `multi_queue`，不代表验证了所有后续版本或定制构建 | [验证边界](protocol-validation.md) |
| CLI | `generate` 与 `profile show --resolved`；既有参数、退出码、三平台目录结构与整批事务替换语义保持稳定 | [CLI 契约](../README.md#输出与事务语义) |
| Behavior | 默认 Ipv4Only 地址族及显式 DirectDualStack/DualStack/Auto、DNS/route 顺序、地区 selector/URLTest、平台 TUN/bridge、默认 Tailscale 与缺失字段 tuning 均是 BoxForge 策略 | [生成配置](generated-config.md)、[DNS 与路由](routing-and-dns.md)、[开关与默认值](configuration.md) |

Core 调用方需使用 `ClashObject.GetRequiredPort` 读取必填端口；原公开方法
`GetRequiredInt` 已更名，取值范围仍为 1～65535。平台保留名称冲突统一抛出
`BoxForgePlatformConversionException`（内部为 `ConfigValidationException`、诊断 `IR009`），
不再因请求的平台集合不同而改为来源解析异常。

手动补充发布说明时，建议记录 Input、Target、CLI、Behavior，尤其说明策略变化
影响的平台、触发条件、前后行为，以及同一 YAML 是否会产生不同配置。
这些说明不作为构建或发布门禁。

固定 BoxForge 版本只消除工具源码漂移。复现生成文件还需要固定输入内容、目标
平台、profile 文件、显式 `--profile` 路径和 `BOXFORGE_` 环境配置。
宿主的无前缀环境变量及 `appsettings*.json` 不参与 CLI、Server 的 profile 解析。
不要把含凭据的 YAML、配置或环境变量值写入公共发布说明或日志。
远程 rule-set、网络环境和 sing-box 本身影响运行行为，文件一致不等于长期网络行为一致。

## 发布流程

1. 按[协议验证边界](protocol-validation.md)完成适用于本次变更的官方核心检查；
   涉及协议时执行对应回环验证，不把单元测试当作设备或真实网络证明。
2. 提交变更后，手动运行 Release workflow 并选择源码分支。无需输入日期 tag；
   默认只构建验证并保存 Actions artifact，不创建 tag 或 GitHub Release。
3. 准备发布时，勾选 `publish_release`。工作流会重新计算日期版本并执行完整测试、
   自包含打包、解包后生成与源码产物比对；全部通过后才为该次构建的固定提交创建
   tag 并发布。可选勾选 `generate_notes`，否则正文为空。
4. 也可继续推送符合日期规范的 tag 触发自动发布，正文默认留空。tag 指向、源码提交、
   包内 manifest 和 SHA-256 仍会核对；已有同名发布不会被覆盖。发布步骤重试时，
   已存在的 tag 只有指向同一个已验证提交才可继续。
5. 发布成功后再更新 BoxVault 的固定版本与 SHA，并检查真实生成流程。

<!-- BEGIN GENERATED:release-gates -->
以下具名构建步骤按 [Release workflow](../.github/workflows/release.yml) 顺序生成：

1. Resolve and validate date tag
2. Build and test
3. Publish self-contained CLI
4. Verify extracted CLI against source output

其中官方核心检查命令为：

```bash
python3 scripts/verify-generated-configs.py src/BoxForge.Cli/bin/Release/net10.0/BoxForge.dll
```

该调用未传 `--core`，验证脚本会自动下载官方 Linux amd64 核心。
锁定版本 `1.15.0-alpha.12`、revision `25b0d5ea3e466d246c708ac0f20d0448110e566c`，
归档 SHA-256 为 `b107528ec7d989333d986573c097d840b9ef1afa9bb177f8eb03e0767f370514`；下载后核验摘要，启动后核验版本和 revision。
这些门禁不替代适用于本次变更的协议回环、设备或真实网络验证。
<!-- END GENERATED:release-gates -->

发布包仅提供 Linux x64（glibc，CI 使用 Ubuntu
24.04），不包含 Server，也不声称支持 Alpine/musl 或其他主机架构。
包内包含 .NET runtime，无需安装 SDK；系统原生依赖仍遵循
[.NET Linux 部署要求](https://learn.microsoft.com/en-us/dotnet/core/install/linux)。
采用常规 self-contained 文件夹打包，不使用 NativeAOT、裁剪或单文件自解压。

发布附件：

- `boxforge-<tag>-linux-x64.tar.gz`：含 `boxforge` 启动文件、依赖、README、docs 和 `RELEASE.json`。
- `SHA256SUMS`：归档校验和，发现损坏或与记录不符；与包同源的校验和不构成独立签名。
- `RELEASE.json`：tag、完整 commit SHA、构建 SDK 和 RID，用于追溯，不记录输入或环境凭据。

## 消费固定版本

源码运行时，将 BoxForge checkout 的 `ref: main` 换为已发布
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

升级时先阅读已有发布说明；说明为空时应自行核对源码变化。在独立输出目录
对比旧版与新版生成结果，再用相应官方
核心检查新产物。确认之后更新消费端版本；不要直接用正式输出目录做升级试验。
