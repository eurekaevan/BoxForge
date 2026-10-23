#!/usr/bin/env python3
"""Exercise generated desktop DNS rules with the pinned core and local TLS DoH servers.

Usage: python3 scripts/verify-dns-races.py /path/to/sing-box /path/to/Linux/config.json
Requires Python 3 and OpenSSL. No TUN, real upstreams, or proxy credentials are used.
"""

import asyncio
import copy
import ipaddress
import json
import pathlib
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import time


CORE_LOCK = json.loads((pathlib.Path(__file__).resolve().parent.parent / "docs/data/sing-box-core.json").read_text(encoding="utf-8"))
VERSION = CORE_LOCK["version"]
REVISION = CORE_LOCK["revision"]

def wire_name(name):
    return b"".join(bytes([len(label)]) + label.encode() for label in name.split(".")) + b"\0"


def question(packet):
    index = 12
    labels = []
    while packet[index]:
        size = packet[index]
        labels.append(packet[index + 1:index + size + 1].decode())
        index += size + 1
    return ".".join(labels), struct.unpack("!H", packet[index + 1:index + 3])[0], index + 5



def answer_ipv4(packet):
    def skip_name(index):
        while packet[index]:
            if packet[index] & 0xC0 == 0xC0:
                return index + 2
            index += packet[index] + 1
        return index + 1

    _, _, questions, answers, _, _ = struct.unpack("!HHHHHH", packet[:12])
    index = 12
    for _ in range(questions):
        index = skip_name(index) + 4
    addresses = []
    for _ in range(answers):
        index = skip_name(index)
        kind, _, _, length = struct.unpack("!HHIH", packet[index:index + 10])
        index += 10
        if kind == 1 and length == 4:
            addresses.append(str(ipaddress.ip_address(packet[index:index + 4])))
        index += length
    return addresses


def response(query, mode, upstream, ttl):
    _, query_type, end = question(query)
    rcode = {"nx": 3, "servfail": 2, "refused": 5}.get(mode, 0)
    answer = b""
    count = 0
    if mode in ("cname", "cname-a"):
        alias = wire_name("alias.google.test")
        answer += b"\xc0\x0c" + struct.pack("!HHIH", 5, 1, ttl, len(alias)) + alias
        count += 1
    if mode in ("a", "cname-a", "additional-a") and query_type == 1:
        owner = wire_name("alias.google.test") if mode == "cname-a" else b"\xc0\x0c"
        answer += owner + struct.pack("!HHIH", 1, 1, ttl, 4)
        answer += ipaddress.ip_address(f"192.0.2.{upstream + 1}").packed
        count += 1
    if mode == "ipv6":
        answer += b"\xc0\x0c" + struct.pack("!HHIH", 28, 1, ttl, 16)
        answer += ipaddress.ip_address("2001:db8::1").packed
        count += 1
    authority = b""
    if mode in ("nx", "empty"):
        soa = b"\0\0" + struct.pack("!IIIII", 1, 60, 60, 60, ttl)
        authority = b"\xc0\x0c" + struct.pack("!HHIH", 6, 1, ttl, len(soa)) + soa
    additional = count if mode == "additional-a" else 0
    header = struct.pack("!HHHHHH", struct.unpack("!H", query[:2])[0],
                         0x8180 | rcode, 1, 0 if additional else count,
                         int(bool(authority)), additional)
    records = authority + answer if additional else answer + authority
    return header + query[12:end] + records


async def main(core, generated):
    version = subprocess.check_output([str(core), "version"], text=True)
    if f"sing-box version {VERSION}\n" not in version or REVISION not in version:
        raise ValueError(f"Expected official sing-box {VERSION} / {REVISION}")
    root = pathlib.Path(tempfile.mkdtemp(prefix="boxforge-dns-races."))
    print(f"Diagnostics: {root}", flush=True)
    cert, key = root / "cert.pem", root / "key.pem"
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                    "-keyout", str(key), "-out", str(cert), "-days", "1",
                    "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost"],
                   check=True, capture_output=True)
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(cert, key)
    tls.set_alpn_protocols(["http/1.1"])
    specs = [(0.03, "a", 60)] * 4
    events = []
    listeners = []
    process = None
    log = None
    try:
        for upstream in range(4):
            async def handle(reader, writer, upstream=upstream):
                try:
                    headers = await reader.readuntil(b"\r\n\r\n")
                    length = int(next(line.split(b":", 1)[1] for line in headers.split(b"\r\n")
                                      if line.lower().startswith(b"content-length:")))
                    packet = await reader.readexactly(length)
                    name, _, _ = question(packet)
                    events.append((name, upstream, time.monotonic()))
                    delay, mode, ttl = specs[upstream]
                    if mode == "drop":
                        await reader.read()
                        return
                    await asyncio.sleep(delay)
                    body = response(packet, mode, upstream, ttl)
                    writer.write(b"HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\n"
                                 b"Connection: close\r\nContent-Length: " + str(len(body)).encode()
                                 + b"\r\n\r\n" + body)
                    await writer.drain()
                except (ConnectionError, asyncio.IncompleteReadError):
                    pass  # The core cancels losing queries by closing their connections.
                finally:
                    writer.close()
            listeners.append(await asyncio.start_server(handle, "127.0.0.1", 0, ssl=tls))

        dns = copy.deepcopy(generated["dns"])
        servers = []
        for server in dns["servers"]:
            if server["type"] == "tailscale":
                continue
            tag = server["tag"]
            if tag == "dns-direct-alidns":
                upstream = 0
            elif tag == "dns-direct-tencent":
                upstream = 1
            elif tag == "dns-proxy-cloudflare" or (tag.startswith("dns-service-") and tag.endswith("-cloudflare")):
                upstream = 2
            elif tag == "dns-proxy-google" or (tag.startswith("dns-service-") and tag.endswith("-google")):
                upstream = 3
            else:
                raise ValueError(f"Unexpected generated DNS server: {tag}")
            servers.append({"tag": tag, "type": "https", "server": "127.0.0.1",
                            "server_port": listeners[upstream].sockets[0].getsockname()[1],
                            "tls": {"enabled": True, "server_name": "localhost", "certificate_path": str(cert)}})
        dns["servers"] = servers
        dns["rules"] = [rule for rule in dns["rules"] if "preferred_by" not in rule]
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        overlap_services = ("spotify", "games", "microsoft")
        rule_sets = [{"type": "inline", "tag": tag,
                      "rules": [{"domain_suffix": [f"{tag}.test"]
                                 + (["overlap.test"] if tag in ("google", "cn") else [])
                                 + [f"{service}-overlap.test" for service in overlap_services
                                    if tag in (service, "cn")]}]}
                     for tag in ("ads", "ai", "google", *overlap_services, "cn")]
        config = {"log": {"level": "debug"}, "dns": dns,
                  "inbounds": [{"type": "direct", "tag": "dns-test", "listen": "127.0.0.1",
                                "listen_port": port, "network": "udp"}],
                  "outbounds": [{"type": "direct", "tag": "DIRECT"}],
                  "route": {"rules": [{"inbound": ["dns-test"], "action": "hijack-dns"}],
                            "rule_set": rule_sets, "final": "DIRECT",
                            "default_domain_resolver": generated["route"]["default_domain_resolver"]}}
        config_path = root / "config.json"
        config_path.write_text(json.dumps(config, indent=2))
        subprocess.run([str(core), "check", "-c", str(config_path)], check=True)
        log = (root / "core.log").open("w")
        process = subprocess.Popen([str(core), "run", "-c", str(config_path)],
                                   stdout=log, stderr=subprocess.STDOUT, cwd=root)
        results = []

        async def query(label, name, query_type=1, timeout=8):
            packet = struct.pack("!HHHHHH", 1234, 0x0100, 1, 0, 0, 0)
            packet += wire_name(name) + struct.pack("!HH", query_type, 1)
            loop = asyncio.get_running_loop()
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
                sock.setblocking(False)
                begin = time.monotonic()
                await loop.sock_sendto(sock, packet, ("127.0.0.1", port))
                data, _ = await asyncio.wait_for(loop.sock_recvfrom(sock, 4096), timeout)
            fields = struct.unpack("!HHHHHH", data[:12])
            result = {"case": label, "ms": round((time.monotonic() - begin) * 1000, 1),
                      "rcode": fields[1] & 15, "answers": fields[3], "ipv4": answer_ipv4(data)}
            results.append(result)
            print(json.dumps(result), flush=True)
            return result

        for attempt in range(20):
            if process.poll() is not None:
                raise RuntimeError(f"Core exited; see {root / 'core.log'}")
            try:
                await query("ready", "ready.google.test", 28, 0.1)
                break
            except asyncio.TimeoutError:
                await asyncio.sleep(0.05)
        else:
            raise RuntimeError("Core did not become ready")
        results.clear()
        # Each row uses a unique name to prevent earlier cases from warming its cache.
        cases = [
            ("fast-primary", "google", "a", "a", .03, .7, 1, 0, 1),
            ("fast-secondary", "google", "a", "a", .7, .03, 1, 0, 1),
            ("failed-primary", "google", "drop", "a", 0, .03, 1, 0, 1),
            ("failed-secondary", "google", "a", "drop", .03, 0, 1, 0, 1),
            ("empty-primary", "google", "empty", "a", .03, .25, 1, 0, 1),
            ("empty-secondary", "google", "a", "empty", .25, .03, 1, 0, 1),
            ("cname-only", "google", "cname", "cname-a", .03, .25, 1, 0, 2),
            ("ipv6-only", "google", "ipv6", "a", .03, .25, 1, 0, 1),
            ("additional-only", "google", "additional-a", "a", .03, .25, 1, 0, 1),
            ("negative-primary", "google", "nx", "a", .03, .25, 1, 0, 1),
            ("negative-secondary", "google", "a", "nx", .25, .03, 1, 0, 1),
            ("both-empty", "google", "empty", "empty", .03, .25, 1, 0, 0),
            ("empty-over-negative", "google", "nx", "empty", .03, .25, 1, 0, 0),
            ("both-negative", "google", "nx", "nx", .03, .25, 1, 3, 0),
            ("service-failure-overlap", "overlap", "servfail", "refused", .03, .05, 1, 2, 0),
            ("domestic-empty-primary", "cn", "empty", "a", .03, .25, 1, 0, 1),
            ("global-empty-primary", "external", "empty", "a", .03, .25, 1, 0, 1),
            ("aaaa", "google", "a", "a", .03, .25, 28, 0, 0),
            ("https-nodata", "google", "empty", "empty", .03, .25, 65, 0, 0),
            ("svcb-nodata", "google", "empty", "empty", .03, .25, 64, 0, 0),
            ("mx-nodata", "google", "empty", "empty", .03, .25, 15, 0, 0),
        ]
        for service in overlap_services:
            cases.extend([
                (f"{service}-cn-overlap", f"{service}-overlap", "a", "a", .03, .05, 1, 0, 1),
                (f"{service}-cn-overlap-failure", f"{service}-overlap", "servfail", "refused", .03, .05, 1, 2, 0),
            ])
        for index, (label, suffix, primary, secondary, pd, sd, qt, rcode, answers) in enumerate(cases):
            specs[:] = [(0.01, "a", 60)] * 4
            pair = (0, 1) if suffix == "cn" else (2, 3)
            specs[pair[0]], specs[pair[1]] = (pd, primary, 60), (sd, secondary, 60)
            name = f"case{index}.{suffix}.test"
            result = await query(label, name, qt)
            assert (result["rcode"], result["answers"]) == (rcode, answers), result
            assert result["ms"] < 1500, result
            if answers:
                winner = pair[0] if primary == "a" and (secondary != "a" or pd < sd) else pair[1]
                assert result["ipv4"] == [f"192.0.2.{winner + 1}"], result
            if label in ("fast-primary", "fast-secondary", "failed-primary", "failed-secondary"):
                assert result["ms"] < 500, "Fast responses must not wait for the 700ms/timeout peer"
            if qt in (15, 64, 65):
                assert result["ms"] < 200, "Non-address queries must still race"
            observed = {event[1] for event in events if event[0] == name}
            assert observed == (set() if qt == 28 else set(pair)), (label, observed)

        specs[2], specs[3] = (0.03, "empty", 1), (0.05, "empty", 1)
        await query("prime-empty-cache", "stale.google.test")
        await asyncio.sleep(1.2)
        specs[2], specs[3] = (0, "drop", 60), (0.1, "a", 60)
        recovered = await query("expired-empty-must-not-win", "stale.google.test")
        assert recovered["ipv4"] == ["192.0.2.4"] and recovered["ms"] < 1500, recovered
        specs[2], specs[3] = (0, "drop", 60), (0, "drop", 60)
        cached = await query("valid-ttl-cache-still-works", "stale.google.test")
        assert cached["ipv4"] == ["192.0.2.4"] and cached["ms"] < 200, cached
        specs[2], specs[3] = (0.03, "nx", 60), (0, "drop", 60)
        negative = await query("negative-timeout-unchanged", "negative.google.test")
        assert negative["rcode"] == 3 and 4500 < negative["ms"] < 6500, negative
        (root / "results.json").write_text(json.dumps(results, indent=2))
        print(f"PASS: {len(results)} local DoH scenarios")
    finally:
        if process is not None:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        if log is not None:
            log.close()
        for listener in listeners:
            listener.close()
            await listener.wait_closed()


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    asyncio.run(main(pathlib.Path(sys.argv[1]).resolve(), json.loads(pathlib.Path(sys.argv[2]).read_text())))
