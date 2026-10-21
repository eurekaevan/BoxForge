#!/usr/bin/env python3
"""Generate documentation fragments from the profile, core lock and release workflow."""

import argparse
import json
from pathlib import Path
import re
import shlex


ROOT = Path(__file__).resolve().parent.parent


def flatten(value, prefix=""):
    fields = {}
    for key, item in value.items():
        path = f"{prefix}.{key}" if prefix else key
        if isinstance(item, dict) and item:
            fields.update(flatten(item, path))
        else:
            fields[path] = item
    return fields


def display(value):
    if isinstance(value, list) and any(isinstance(item, dict) for item in value):
        return f"{len(value)} 项（见内置 profile）"
    return "`" + json.dumps(value, ensure_ascii=False, separators=(",", ":")).replace("|", "\\|") + "`"


def fragments():
    profile = json.loads((ROOT / "src/BoxForge.Core/Configuration/default-profile.json").read_text(encoding="utf-8"))
    core = json.loads((ROOT / "docs/data/sing-box-core.json").read_text(encoding="utf-8"))
    if (set(core) != {"version", "revision", "archive_sha256"}
            or not isinstance(core["version"], str) or not core["version"].strip()
            or not re.fullmatch(r"[0-9a-f]{40}", core["revision"])
            or not re.fullmatch(r"[0-9a-f]{64}", core["archive_sha256"])):
        raise ValueError("Expected a core version, full revision and SHA-256 archive digest")
    defaults = flatten({key: value for key, value in profile.items() if key != "platforms"})
    platforms = {name: flatten(layer) for name, layer in profile["platforms"].items()}
    fields = dict.fromkeys([*defaults, *(key for layer in platforms.values() for key in layer)])
    table = [
        "由[内置 profile 源码](https://github.com/eurekaevan/BoxForge/blob/main/src/BoxForge.Core/Configuration/default-profile.json)生成；平台列仅列出内置覆盖，未列出的值继承根默认值。",
        "对象数组的完整内容见内置 profile；下表的数组是默认数据，覆盖操作见后文。", "",
        "更新事实来源后运行 `python3 scripts/generate-contract-docs.py`；CI 使用 `--check` 拒绝过期片段。", "",
        "| 配置路径 | 根默认值 | 平台覆盖 |", "| --- | --- | --- |"
    ]
    for key in fields:
        overrides = "；".join(f"{name}: {display(layer[key])}" for name, layer in platforms.items() if key in layer)
        table.append(f"| `{key}` | {display(defaults[key]) if key in defaults else '未设置'} | {overrides or '—'} |")

    api_path = ("features", "api", "enabled")
    api_default = defaults[".".join(api_path)]
    api = (f"API 默认 `{'.'.join(api_path)} = {json.dumps(api_default)}`。将该键设为 `true`\n"
           f"（环境变量 `BOXFORGE_{'__'.join(api_path)}=true`）后，顶层增加一个仅监听\n"
           "`127.0.0.1:9090` 的 `api` service，并启用工作目录下的 `dashboard`。")
    baseline = (f"当前最低支持与资格验证核心为官方 sing-box `{core['version']}`，\n"
                f"revision `{core['revision']}`。版本、revision 与归档摘要统一来自\n"
                "[核心锁定数据](data/sing-box-core.json)；不代表验证了所有后续版本或定制构建。")

    # Match the repository's literal run commands, without interpreting shell or YAML.
    # Unsupported job layouts and conditional gates require an explicit documentation update.
    workflow = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")
    match = re.search(r"^  build:\n(.*?)(?=^  [\w-]+:|\Z)", workflow, re.MULTILINE | re.DOTALL)
    if match is None:
        raise ValueError("Expected a literal Release build job")
    build = match[1]
    if re.search(r"^\s+(?:if|continue-on-error):", build, re.MULTILINE):
        raise ValueError("Conditional or allowed-to-fail Release build gates need explicit documentation support")
    commands = re.findall(r"^\s*(python3 scripts/verify-generated-configs\.py [^\n]+)$", build, re.MULTILINE)
    if len(commands) != 1 or commands[0].endswith("\\"):
        raise ValueError("Expected one literal official-core command in the Release build job")
    command = commands[0]
    names = re.findall(r"^      - name: (.+)$", build, re.MULTILINE)
    if not names:
        raise ValueError("Release build job has no named steps")
    supplied_core = any(arg == "--core" or arg.startswith("--core=") for arg in shlex.split(command))
    gates = [
        "以下具名构建步骤按 [Release workflow](../.github/workflows/release.yml) 顺序生成：", "",
        *(f"{index}. {name}" for index, name in enumerate(names, 1)), "",
        "其中官方核心检查命令为：", "", "```bash", command, "```", "",
        "该调用通过 `--core` 使用已有核心。" if supplied_core else
        "该调用未传 `--core`，验证脚本会自动下载官方 Linux amd64 核心。",
        f"锁定版本 `{core['version']}`、revision `{core['revision']}`，",
        f"归档 SHA-256 为 `{core['archive_sha256']}`；下载后核验摘要，启动后核验版本和 revision。",
        "这些门禁不替代适用于本次变更的协议回环、设备或真实网络验证。"
    ]
    return {
        "docs/configuration.md": {"profile-defaults": "\n".join(table)},
        "docs/generated-config.md": {"api-setting": api, "core-baseline": baseline},
        "docs/protocol-validation.md": {"core-baseline": baseline},
        "docs/routing-and-dns.md": {"core-baseline": baseline},
        "docs/releases.md": {"release-gates": "\n".join(gates)},
        "README.md": {"minimum-core":
            f"需要 .NET SDK 10.0。生成配置当前最低要求为官方 sing-box `{core['version']}`，\n"
            "统一覆盖 Tailscale endpoint 的 `on_demand` 和 Linux TUN 的 `multi_queue`；\n"
            "不承诺更早 alpha 版本兼容。锁定版本与 revision 见[核心数据](docs/data/sing-box-core.json)。"}
    }


def update(content, name, fragment):
    start = f"<!-- BEGIN GENERATED:{name} -->"
    end = f"<!-- END GENERATED:{name} -->"
    pattern = re.escape(start) + r"\n.*?\n" + re.escape(end)
    if content.count(start) != 1 or content.count(end) != 1:
        raise ValueError(f"Expected exactly one marker pair for {name}")
    result, count = re.subn(pattern, lambda _: f"{start}\n{fragment}\n{end}", content, flags=re.DOTALL)
    if count != 1:
        raise ValueError(f"Invalid marker order for {name}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Fail instead of writing stale fragments")
    arguments = parser.parse_args()
    stale = []
    for relative, sections in fragments().items():
        path = ROOT / relative
        original = path.read_text(encoding="utf-8")
        content = original
        for name, fragment in sections.items():
            content = update(content, name, fragment)
        if content != original:
            stale.append(relative)
            if not arguments.check:
                path.write_text(content, encoding="utf-8")
    if stale and arguments.check:
        parser.exit(1, f"Stale contract docs: {', '.join(stale)}; run python3 scripts/generate-contract-docs.py\n")
    print("Contract documentation is up to date." if arguments.check else f"Updated {len(stale)} contract documents.")


if __name__ == "__main__":
    main()
