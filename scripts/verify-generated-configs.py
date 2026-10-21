#!/usr/bin/env python3
"""Check generated configurations and representative invalid cases with the pinned core.

Usage: python3 scripts/verify-generated-configs.py path/to/BoxForge.dll [--core path/to/sing-box]
The default downloads the official Linux amd64 core into a temporary directory.
"""

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request


CORE_LOCK = json.loads((Path(__file__).resolve().parent.parent / "docs/data/sing-box-core.json").read_text(encoding="utf-8"))
VERSION = CORE_LOCK["version"]
REVISION = CORE_LOCK["revision"]
ARCHIVE_NAME = f"sing-box-{VERSION}-linux-amd64.tar.gz"
# Official release asset digest, pinned with its version and revision in sing-box-core.json.
ARCHIVE_SHA256 = CORE_LOCK["archive_sha256"]
ARCHIVE_URL = f"https://github.com/SagerNet/sing-box/releases/download/v{VERSION}/{ARCHIVE_NAME}"


VERSION_TIMEOUT = 10
GENERATION_TIMEOUT = 60
CHECK_TIMEOUT = 30


def run_command(command, root, context, *, timeout=CHECK_TIMEOUT, environment=None, expected_error=None):
    try:
        result = subprocess.run(command, cwd=root, env=environment, timeout=timeout,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    except subprocess.TimeoutExpired as error:
        output = error.stdout or ""
        if isinstance(output, bytes):
            output = output.decode("utf-8", errors="replace")
        raise RuntimeError(f"{context}: timed out after {timeout}s\n{output}") from error
    except OSError as error:
        raise RuntimeError(f"{context}: could not start process: {error}") from error
    if result.returncode < 0:
        raise RuntimeError(f"{context}: terminated by signal {-result.returncode}\n{result.stdout}")
    if expected_error is not None:
        if result.returncode != 1 or expected_error not in result.stdout:
            raise RuntimeError(f"{context}: expected exit 1 and diagnostic {expected_error!r}; "
                               f"received exit {result.returncode}\n{result.stdout}")
    elif result.returncode != 0:
        raise RuntimeError(f"{context}: exited with status {result.returncode}\n{result.stdout}")
    return result.stdout


def download_core(root):
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
        raise RuntimeError("Automatic core download requires Linux amd64; pass --core on other hosts.")
    archive = root / ARCHIVE_NAME
    with urllib.request.urlopen(ARCHIVE_URL, timeout=120) as response, archive.open("wb") as output:
        shutil.copyfileobj(response, output)
    with archive.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if digest != ARCHIVE_SHA256:
        raise RuntimeError(f"Official core archive SHA256 mismatch: {digest}")
    core = root / "sing-box"
    with tarfile.open(archive, "r:gz") as bundle:
        member = bundle.getmember(f"sing-box-{VERSION}-linux-amd64/sing-box")
        if not member.isfile():
            raise RuntimeError("Official core archive does not contain a regular sing-box binary.")
        with bundle.extractfile(member) as source, core.open("wb") as output:
            shutil.copyfileobj(source, output)
    core.chmod(0o755)
    return core


def verify_rejections(core, root, generated):
    baseline = json.loads((generated / "profiles/Linux/config.json").read_text())
    route_cases = (
        ("invalid-ip-version", {"ip_version": 5, "action": "reject"}, "parse rule[{index}]: invalid ip version: 5"),
        ("no-drop-on-route", {"action": "route", "outbound": "DIRECT", "no_drop": True}, 'route.rules[{index}].no_drop: json: unknown field "no_drop"'),
        ("strategy-on-route", {"action": "route", "outbound": "DIRECT", "strategy": "ipv4_only"}, 'route.rules[{index}].strategy: json: unknown field "strategy"'),
        ("outbound-on-sniff", {"action": "sniff", "outbound": "DIRECT"}, 'route.rules[{index}].outbound: json: unknown field "outbound"'),
    )
    cases = []
    for name, rule, diagnostic in route_cases:
        config = copy.deepcopy(baseline)
        config["route"]["rules"].append(rule)
        index = len(config["route"]["rules"]) - 1
        cases.append((name, config, diagnostic.format(index=index)))
    config = copy.deepcopy(baseline)
    group_index, grouped = next((index, item) for index, item in enumerate(config["route"]["rule_set"])
                                if isinstance(item["tag"], list))
    grouped["url"] = grouped["url"].replace("{tag}", "constant")
    cases.append(("grouped-rule-set-without-tag-placeholder", config,
                  f"route.rule_set[{group_index}]: missing {{tag}} placeholder in url"))
    config = json.loads((generated / "tls/Linux/config.json").read_text())
    outbound_index, vless = next((index, item) for index, item in enumerate(config["outbounds"])
                                 if item["type"] == "vless")
    vless["flow"] = "bad"
    cases.append(("invalid-vless-flow", config, f"initialize outbound[{outbound_index}]: unsupported flow: bad"))
    for name, config, diagnostic in cases:
        path = root / f"invalid-{name}.json"
        path.write_text(json.dumps(config))
        run_command([str(core), "check", "-c", str(path)], root,
                    f"Official core negative case {name}", expected_error=diagnostic)
        print(f"Official core rejected: {name}")
    return len(cases)


def verify(cli, supplied_core):
    repository = Path(__file__).resolve().parent.parent
    fixtures = repository / "Tests/BoxForge.Tests/Fixtures"
    with tempfile.TemporaryDirectory(prefix="boxforge-core-check-") as temporary:
        root = Path(temporary)
        # Keep committed fixtures and their byte baselines unchanged.
        fixtures = Path(shutil.copytree(fixtures, root / "fixtures"))
        port_cases = {
            "hy2-port-scalar": ("443", None),
            "hy2-port-equal-range": ("443-443", ["443:443"]),
            "hy2-port-list": ("443,8443", ["443:443", "8443:8443"]),
            "hy2-port-mixed": ("443,8443-8444", ["443:443", "8443:8444"]),
            "hy2-port-range-first": ("443-444,8443", ["443:444", "8443:8443"]),
            "hy2-port-duplicates": ("1,65535,1", ["1:1", "65535:65535", "1:1"]),
        }
        with (fixtures / "hysteria2.yaml").open("a", encoding="utf-8") as source:
            for tag, (ports, _) in port_cases.items():
                source.write(f"\n  - name: {tag}\n    type: hysteria2\n    server: node.example.com\n"
                             f"    port: 0\n    ports: {json.dumps(ports)}\n    password: test-only\n")
        core = supplied_core or download_core(root)
        version = run_command([str(core), "version"], root, "Official core version", timeout=VERSION_TIMEOUT)
        if f"sing-box version {VERSION}\n" not in version or REVISION not in version:
            raise RuntimeError(f"Expected official sing-box {VERSION} / {REVISION}; found:\n{version}")
        count = 0
        for tailscale in (False, True):
            for api in (False, True):
                output = root / f"tailscale-{tailscale}-api-{api}"
                # Verification must not inherit a user profile or host overrides.
                environment = {key: value for key, value in os.environ.items()
                               if not key.upper().startswith("BOXFORGE_")}
                environment.update({
                    "BOXFORGE_features__tailscale__enabled": json.dumps(tailscale),
                    "BOXFORGE_features__api__enabled": json.dumps(api),
                })
                run_command([
                    "dotnet", str(cli), "generate", "--input-dir", str(fixtures),
                    "--output-dir", str(output), "--platform", "all",
                ], root, f"CLI generation tailscale={tailscale} api={api}",
                   timeout=GENERATION_TIMEOUT, environment=environment)
                configs = sorted(output.glob("*/*/config.json"))
                if len(configs) != 15:
                    raise RuntimeError(f"Expected 5 fixtures × 3 platforms, found {len(configs)} in {output}")
                for config in configs:
                    if config.parent.parent.name == "hysteria2":
                        leaves = {item["tag"]: item for item in json.loads(config.read_text())["outbounds"]}
                        for tag, (_, expected) in port_cases.items():
                            leaf = leaves[tag]
                            if expected is None:
                                valid = leaf.get("server_port") == 443 and "server_ports" not in leaf
                            else:
                                valid = leaf.get("server_ports") == expected and "server_port" not in leaf
                            if not valid:
                                raise RuntimeError(f"Incorrect Hysteria2 port representation for {tag} in {config}")
                    run_command([str(core), "check", "-c", str(config)], root,
                                f"Official core positive case {config.relative_to(output)}")
                    count += 1
        rejected = verify_rejections(core, root, output)
        print(f"Official sing-box {VERSION} check passed: {count} generated configurations, {rejected} invalid configurations rejected.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("cli", type=Path, help="Path to the already built BoxForge.dll")
    parser.add_argument("--core", type=Path, help="Use an existing pinned official sing-box binary")
    arguments = parser.parse_args()
    cli = arguments.cli.resolve(strict=True)
    core = arguments.core.resolve(strict=True) if arguments.core else None
    verify(cli, core)


if __name__ == "__main__":
    main()
