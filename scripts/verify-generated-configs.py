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


VERSION = "1.15.0-alpha.12"
REVISION = "25b0d5ea3e466d246c708ac0f20d0448110e566c"
ARCHIVE_NAME = f"sing-box-{VERSION}-linux-amd64.tar.gz"
# Official release asset digest: https://api.github.com/repos/SagerNet/sing-box/releases/tags/v1.15.0-alpha.12
ARCHIVE_SHA256 = "b107528ec7d989333d986573c097d840b9ef1afa9bb177f8eb03e0767f370514"
ARCHIVE_URL = f"https://github.com/SagerNet/sing-box/releases/download/v{VERSION}/{ARCHIVE_NAME}"


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
        ("invalid-ip-version", {"ip_version": 5, "action": "reject"}),
        ("no-drop-on-route", {"action": "route", "outbound": "DIRECT", "no_drop": True}),
        ("strategy-on-route", {"action": "route", "outbound": "DIRECT", "strategy": "ipv4_only"}),
        ("outbound-on-sniff", {"action": "sniff", "outbound": "DIRECT"}),
    )
    cases = []
    for name, rule in route_cases:
        config = copy.deepcopy(baseline)
        config["route"]["rules"].append(rule)
        cases.append((name, config))
    config = copy.deepcopy(baseline)
    grouped = next(item for item in config["route"]["rule_set"] if isinstance(item["tag"], list))
    grouped["url"] = grouped["url"].replace("{tag}", "constant")
    cases.append(("grouped-rule-set-without-tag-placeholder", config))
    config = json.loads((generated / "tls/Linux/config.json").read_text())
    next(item for item in config["outbounds"] if item["type"] == "vless")["flow"] = "bad"
    cases.append(("invalid-vless-flow", config))
    for name, config in cases:
        path = root / f"invalid-{name}.json"
        path.write_text(json.dumps(config))
        result = subprocess.run([str(core), "check", "-c", str(path)], cwd=root, capture_output=True)
        if result.returncode == 0:
            raise RuntimeError(f"Official core unexpectedly accepted negative case: {name}")
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
        version = subprocess.check_output([str(core), "version"], text=True, cwd=root)
        if f"sing-box version {VERSION}\n" not in version or REVISION not in version:
            raise RuntimeError(f"Expected official sing-box {VERSION} / {REVISION}; found:\n{version}")
        count = 0
        for tailscale in (False, True):
            for api in (False, True):
                output = root / f"tailscale-{tailscale}-api-{api}"
                environment = os.environ.copy()
                environment.update({
                    "BOXFORGE_Tailscale__Enabled": str(tailscale),
                    "BOXFORGE_Tailscale__AndroidEnabled": str(tailscale),
                    "BOXFORGE_SingboxApi__Enabled": str(api),
                })
                subprocess.run([
                    "dotnet", str(cli), "generate", "--input-dir", str(fixtures),
                    "--output-dir", str(output), "--platform", "all",
                ], cwd=root, env=environment, check=True)
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
                    subprocess.run([str(core), "check", "-c", str(config)], cwd=root, check=True)
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
