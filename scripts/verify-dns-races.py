#!/usr/bin/env python3
"""Exercise generated DNS rules with the pinned core and local TLS DoH servers.

Usage: python3 scripts/verify-dns-races.py /path/to/sing-box /path/to/platform/config.json
Requires generated Ipv4Only or DualStack DNS with a 3s query timeout,
the default services/domain blocks, Python 3 and OpenSSL.
No TUN, real upstreams, or proxy credentials are used.
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


NON_ADDRESS_RECORDS = {
    12: wire_name("ptr.example.test"),
    15: struct.pack("!H", 10) + wire_name("mail.example.test"),
    16: b"\x04test",
    33: struct.pack("!HHH", 1, 0, 443) + wire_name("service.example.test"),
    64: b"\x00\x01\x00\x00\x01\x00\x03\x02h2",
    65: (b"\x00\x01\x00\x00\x01\x00\x03\x02h2"
         + struct.pack("!HH", 4, 4) + ipaddress.ip_address("192.0.2.1").packed
         + struct.pack("!HH", 6, 16) + ipaddress.ip_address("2001:db8::1").packed),
    65280: b"opaque-private-record",
}


def question(packet):
    index = 12
    labels = []
    while packet[index]:
        size = packet[index]
        labels.append(packet[index + 1:index + size + 1].decode())
        index += size + 1
    return ".".join(labels), struct.unpack("!H", packet[index + 1:index + 3])[0], index + 5



def answer_values(packet, query_type):
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
    values = []
    for _ in range(answers):
        index = skip_name(index)
        kind, _, _, length = struct.unpack("!HHIH", packet[index:index + 10])
        index += 10
        if kind == query_type:
            value = packet[index:index + length]
            values.append(str(ipaddress.ip_address(value)) if kind in (1, 28) else value.hex())
        index += length
    return values


def response(query, mode, upstream, ttl):
    _, query_type, end = question(query)
    rcode = {"nx": 3, "servfail": 2, "refused": 5}.get(mode, 0)
    answer = b""
    count = 0
    if mode == "record":
        data = NON_ADDRESS_RECORDS[query_type]
        answer = b"\xc0\x0c" + struct.pack("!HHIH", query_type, 1, ttl, len(data)) + data
        count = 1
    if mode in ("cname", "cname-a", "cname-aaaa"):
        alias = wire_name("alias.google.test")
        answer += b"\xc0\x0c" + struct.pack("!HHIH", 5, 1, ttl, len(alias)) + alias
        count += 1
    if mode in ("a", "cname-a", "additional-a") and query_type == 1:
        owner = wire_name("alias.google.test") if mode == "cname-a" else b"\xc0\x0c"
        answer += owner + struct.pack("!HHIH", 1, 1, ttl, 4)
        answer += ipaddress.ip_address(f"192.0.2.{upstream + 1}").packed
        count += 1
    if mode in ("aaaa", "cname-aaaa", "additional-aaaa") and query_type == 28:
        owner = wire_name("alias.google.test") if mode == "cname-aaaa" else b"\xc0\x0c"
        answer += owner + struct.pack("!HHIH", 28, 1, ttl, 16)
        answer += ipaddress.ip_address(f"2001:db8::{upstream + 1}").packed
        count += 1
    if mode == "ipv4":
        answer += b"\xc0\x0c" + struct.pack("!HHIH", 1, 1, ttl, 4)
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
    additional = count if mode in ("additional-a", "additional-aaaa") else 0
    header = struct.pack("!HHHHHH", struct.unpack("!H", query[:2])[0],
                         0x8180 | rcode, 1, 0 if additional else count,
                         int(bool(authority)), additional)
    records = authority + answer if additional else answer + authority
    return header + query[12:end] + records


async def main(core, generated):
    dns = generated["dns"]
    parallel = any(rule.get("race") is True for rule in dns.get("rules", []))
    ipv4_only = dns.get("strategy") == "ipv4_only"
    address_queries = [("A", "0.0.0.0/0")] + ([] if ipv4_only else [("AAAA", "::/0")])
    if (dns.get("strategy") not in ("ipv4_only", None)
            or dns.get("timeout") != "3s"
            or (parallel and not all(any(rule.get("action") == "respond" and rule.get("race") is True
                           and rule.get("query_type") == [query_type] and rule.get("ip_cidr") == [cidr]
                           for rule in dns.get("rules", []))
                       for query_type, cidr in address_queries))
            or (not parallel and not any(rule.get("action") == "evaluate" and rule.get("timeout") == "2s"
                                         for rule in dns.get("rules", [])))):
        raise ValueError("DNS verification requires Ipv4Only or DualStack + ParallelFastest or SequentialFallback DNS with a 3s query timeout")
    version = subprocess.check_output([str(core), "version"], text=True, timeout=10)
    if f"sing-box version {VERSION}\n" not in version or REVISION not in version:
        raise ValueError(f"Expected official sing-box {VERSION} / {REVISION}")
    root = pathlib.Path(tempfile.mkdtemp(prefix="boxforge-dns-races."))
    print(f"Diagnostics: {root}", flush=True)
    cert, key = root / "cert.pem", root / "key.pem"
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                    "-keyout", str(key), "-out", str(cert), "-days", "1",
                    "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost"],
                   check=True, capture_output=True, timeout=30)
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
                    events.append((name, upstream))
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
            elif tag == "dns-direct-alidns-secondary":
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
        overlap_services = ("github", "spotify", "games")
        rule_sets = [{"type": "inline", "tag": tag,
                      "rules": [{"domain_suffix": [f"{tag}.test"]
                                 + (["biliapi.net", "bilivideo.com", "bilivideo.cn", "szbdyd.com", "mountaintoys.cn"] if tag == "cn" else [])
                                 + (["overlap.test"] if tag in ("google", "cn") else [])
                                 + [f"{service}-overlap.test" for service in overlap_services
                                    if tag in (service, "cn")]}]}
                     for tag in ("ads", "ai", "google", *overlap_services, "cn")]
        config = {"log": {"level": "debug"}, "dns": dns,
                  "experimental": copy.deepcopy(generated["experimental"]),
                  "inbounds": [{"type": "direct", "tag": "dns-test", "listen": "127.0.0.1",
                                "listen_port": port, "network": "udp"}],
                  "outbounds": [{"type": "direct", "tag": "DIRECT"}],
                  "route": {"rules": [{"inbound": ["dns-test"], "action": "hijack-dns"}],
                            "rule_set": rule_sets, "final": "DIRECT",
                            "default_domain_resolver": generated["route"]["default_domain_resolver"]}}
        config_path = root / "config.json"
        config_path.write_text(json.dumps(config, indent=2))
        subprocess.run([str(core), "check", "-c", str(config_path)], check=True, timeout=30)
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
                      "rcode": fields[1] & 15, "answers": fields[3], "ipv4": answer_values(data, 1), "ipv6": answer_values(data, 28),
                      "records": answer_values(data, query_type)}
            results.append(result)
            print(json.dumps(result), flush=True)
            return result

        for attempt in range(20):
            if process.poll() is not None:
                raise RuntimeError(f"Core exited; see {root / 'core.log'}")
            try:
                await query("ready", "ready.google.test", 1, 0.1)
                break
            except asyncio.TimeoutError:
                await asyncio.sleep(0.05)
        else:
            raise RuntimeError("Core did not become ready")
        results.clear()
        blocked_names = [
            "foo-pcdn-bar.biliapi.net", "pcdn.biliapi.net", "PCDN.BILIAPI.NET",
            "nested.foo-pcdn.biliapi.net", "mcdn.bilivideo.com", "a.mcdn.bilivideo.com",
            "mcdn.bilivideo.cn", "a.szbdyd.com", "mountaintoys.cn"
        ]
        for name in blocked_names:
            for qt in (1, 65):
                result = await query(f"blocked-{name}-{qt}", name, qt)
                assert (result["rcode"], result["answers"]) == (3, 0), result
                assert not any(event[0] == name for event in events), (name, events)
        for name in ("normal.biliapi.net", "pcdn.biliapi.net.evil", "notmcdn.bilivideo.com", "bilivideo.com"):
            specs[2], specs[3] = (0.03, "a", 60), (0.7, "a", 60)
            result = await query(f"allowed-{name}", name)
            assert (result["rcode"], result["answers"]) == (0, 1), result
            assert any(event[0] == name for event in events), name
        # Each row uses a unique name to prevent earlier cases from warming its cache.
        cases = [
            ("fast-primary", "google", "a", "a", .03, .7, 1, 0, 1),
            ("fast-secondary", "google", "a", "a", .7, .03, 1, 0, 1),
            ("failed-primary", "google", "drop", "a", 0, .03, 1, 0, 1),
            ("failed-secondary", "google", "a", "drop", .03, 0, 1, 0, 1),
            ("slow-positive", "google", "drop", "a", 0, 1.5, 1, 0, 1),
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
        if not ipv4_only:
            cases.extend([
                ("aaaa-fast-primary", "google", "aaaa", "aaaa", .03, .7, 28, 0, 1),
                ("aaaa-fast-secondary", "google", "aaaa", "aaaa", .7, .03, 28, 0, 1),
                ("aaaa-failed-primary", "google", "drop", "aaaa", 0, .03, 28, 0, 1),
                ("aaaa-failed-secondary", "google", "aaaa", "drop", .03, 0, 28, 0, 1),
                ("aaaa-slow-positive", "google", "drop", "aaaa", 0, 1.5, 28, 0, 1),
                ("aaaa-empty-primary", "google", "empty", "aaaa", .03, .25, 28, 0, 1),
                ("aaaa-empty-secondary", "google", "aaaa", "empty", .25, .03, 28, 0, 1),
                ("aaaa-cname-only", "google", "cname", "cname-aaaa", .03, .25, 28, 0, 2),
                ("aaaa-ipv4-only", "google", "ipv4", "aaaa", .03, .25, 28, 0, 1),
                ("aaaa-additional-only", "google", "additional-aaaa", "aaaa", .03, .25, 28, 0, 1),
                ("aaaa-negative-primary", "google", "nx", "aaaa", .03, .25, 28, 0, 1),
                ("aaaa-negative-secondary", "google", "aaaa", "nx", .25, .03, 28, 0, 1),
                ("aaaa-both-empty", "google", "empty", "empty", .03, .25, 28, 0, 0),
                ("aaaa-empty-over-negative", "google", "nx", "empty", .03, .25, 28, 0, 0),
                ("aaaa-both-negative", "google", "nx", "nx", .03, .25, 28, 3, 0),
                ("aaaa-domestic-empty", "cn", "empty", "aaaa", .03, .25, 28, 0, 1),
                ("aaaa-global-empty", "external", "empty", "aaaa", .03, .25, 28, 0, 1),
                ("aaaa-service-failure-overlap", "overlap", "servfail", "refused", .03, .05, 28, 2, 0),
            ])
        for index, (label, suffix, primary, secondary, pd, sd, qt, rcode, answers) in enumerate(cases if parallel else []):
            specs[:] = [(0.01, "a", 60)] * 4
            pair = (0, 1) if suffix == "cn" else (2, 3)
            specs[pair[0]], specs[pair[1]] = (pd, primary, 60), (sd, secondary, 60)
            name = f"case{index}.{suffix}.test"
            result = await query(label, name, qt)
            assert (result["rcode"], result["answers"]) == (rcode, answers), result
            if label.removeprefix("aaaa-") == "slow-positive":
                assert 1200 < result["ms"] < 2500, result
            else:
                assert result["ms"] < 1500, result
            if answers:
                address_mode = "a" if qt == 1 else "aaaa"
                winner = pair[0] if primary == address_mode and (secondary != address_mode or pd < sd) else pair[1]
                field = "ipv4" if qt == 1 else "ipv6"
                address = f"192.0.2.{winner + 1}" if qt == 1 else f"2001:db8::{winner + 1}"
                assert result[field] == [address], result
            if label.removeprefix("aaaa-") in ("fast-primary", "fast-secondary", "failed-primary", "failed-secondary"):
                assert result["ms"] < 500, "Fast responses must not wait for a slower or timed-out peer"
            if qt in (15, 64, 65):
                assert result["ms"] < 200, "Non-address queries must still race"
            observed = {event[1] for event in events if event[0] == name}
            assert observed == (set() if qt == 28 and ipv4_only else set(pair)), (label, observed)

        # Non-address replies preserve RDATA, except the core removes HTTPS ipv6hint in IPv4-only mode.
        for suffix in ("cn", "google", "external"):
            pair = (0, 1) if suffix == "cn" else (2, 3)
            # Label, response modes, delays, expected rcode/answer count, latency bounds in ms.
            scenarios = [
                ("positive", "record", "record", .03, .7, 0, 1, 0, 500),
                ("fast-secondary", "record", "record", .7, .03, 0, 1, 0, 500 if parallel else 1500),
                ("empty-primary", "empty", "record", .03, .25, 0, 0, 0, 500),
                ("empty-secondary", "record", "empty", .25, .03, 0, 0 if parallel else 1, 0, 500),
                ("negative-primary", "nx", "record", .03, .25, 0 if parallel else 3, 1 if parallel else 0, 0, 1500),
                ("negative-secondary", "record", "nx", .25, .03, 0, 1, 0, 500),
                ("failed-primary", "servfail", "record", .03, .05, 0, 1, 0, 1500),
                ("timeout-primary", "drop", "record", 0, .03, 0, 1, 0 if parallel else 1800, 500 if parallel else 2800),
                ("timeout-secondary", "record", "drop", .03, 0, 0, 1, 0, 500),
                ("both-negative", "nx", "nx", .03, .25, 3, 0, 0, 1500),
                ("both-empty", "empty", "empty", .03, .25, 0, 0, 0, 500),
                ("negative-timeout", "nx", "drop", .03, 0, 3, 0, 2800 if parallel else 0, 3800 if parallel else 500),
                ("both-failure", "servfail", "refused", .03, .05, 2, 0, 0, 1500),
                ("both-timeout", "drop", "drop", 0, 0, 2, 0, 2800 if parallel else 4800, 3800 if parallel else 5800),
            ]
            for qt in NON_ADDRESS_RECORDS:
                for label, primary, secondary, pd, sd, rcode, answers, minimum, maximum in scenarios:
                    # HTTPS exercises every failure path; other types exercise payload and empty-answer semantics.
                    if qt != 65 and label not in ("positive", "fast-secondary", "empty-primary", "empty-secondary", "both-empty"):
                        continue
                    specs[:] = [(0.01, "record", 60)] * 4
                    specs[pair[0]], specs[pair[1]] = (pd, primary, 60), (sd, secondary, 60)
                    name = f"non-address-{qt}-{label}.{suffix}.test"
                    if qt == 65:
                        name = "_7826._https." + name
                    result = await query(f"{suffix}-{qt}-{label}", name, qt)
                    assert (result["rcode"], result["answers"]) == (rcode, answers), result
                    assert minimum <= result["ms"] < maximum, result
                    expected_data = NON_ADDRESS_RECORDS[qt]
                    if qt == 65 and ipv4_only:
                        expected_data = expected_data[:-20]  # ipv6hint key + length + 16-byte address
                    assert result["records"] == ([expected_data.hex()] if answers else []), result
                    observed = {event[1] for event in events if event[0] == name}
                    expected = set(pair) if parallel or primary not in ("record", "empty", "nx") else {pair[0]}
                    assert observed == expected, (result, observed)

        for qt in (16, 65):
            for negative_mode in ("empty", "nx"):
                name = f"expired-{negative_mode}-{qt}.google.test"
                specs[2], specs[3] = (0.03, negative_mode, 1), (0.05, negative_mode, 1)
                primed = await query(f"prime-{negative_mode}-{qt}", name, qt)
                assert (primed["rcode"], primed["answers"]) == (3 if negative_mode == "nx" else 0, 0), primed
                await asyncio.sleep(1.2)
                specs[2], specs[3] = (0, "drop", 60), (0.1, "record", 60)
                recovered = await query(f"expired-{negative_mode}-{qt}-must-not-win", name, qt)
                assert (recovered["rcode"], recovered["answers"]) == (0, 1), recovered
                assert recovered["ms"] < (500 if parallel else 2800), recovered
                specs[2], specs[3] = (0, "drop", 60), (0, "drop", 60)
                secondary_queries = events.count((name, 3))
                cached = await query(f"valid-ttl-{qt}-{negative_mode}-still-works", name, qt)
                assert cached["records"] == recovered["records"], cached
                # Sequential fallback still spends primary's budget before reading secondary's cache.
                assert cached["ms"] < (200 if parallel else 2800), cached
                assert events.count((name, 3)) == secondary_queries, cached

        specs[2], specs[3] = (0.03, "record", 1), (0.05, "record", 1)
        primed = await query("prime-https-positive", "expired-positive.google.test", 65)
        assert (primed["rcode"], primed["answers"]) == (0, 1), primed
        await asyncio.sleep(1.2)
        specs[2], specs[3] = (0, "drop", 60), (0, "drop", 60)
        expired = await query("expired-https-positive-must-not-win", "expired-positive.google.test", 65)
        assert (expired["rcode"], expired["answers"]) == (2, 0), expired
        assert (2800 if parallel else 4800) < expired["ms"] < (3800 if parallel else 5800), expired

        cache_cases = [(1, "a", "ipv4", "192.0.2.4")] + (
            [] if ipv4_only else [(28, "aaaa", "ipv6", "2001:db8::4")])
        for qt, mode, field, expected in cache_cases if parallel else []:
            prefix = "" if qt == 1 else "aaaa-"
            stale_name = f"{prefix}stale.google.test"
            specs[2], specs[3] = (0.03, "empty", 1), (0.05, "empty", 1)
            primed = await query(prefix + "prime-empty-cache", stale_name, qt)
            assert (primed["rcode"], primed["answers"]) == (0, 0), primed
            await asyncio.sleep(1.2)
            specs[2], specs[3] = (0, "drop", 60), (0.1, mode, 60)
            recovered = await query(prefix + "expired-empty-must-not-win", stale_name, qt)
            assert recovered[field] == [expected] and recovered["ms"] < 1500, recovered
            specs[2], specs[3] = (0, "drop", 60), (0, "drop", 60)
            cached = await query(prefix + "valid-ttl-cache-still-works", stale_name, qt)
            assert cached[field] == [expected] and cached["ms"] < 200, cached
            specs[2], specs[3] = (0.03, "nx", 60), (0, "drop", 60)
            negative = await query(prefix + "negative-timeout-budget", f"{prefix}negative.google.test", qt)
            assert negative["rcode"] == 3 and 2800 < negative["ms"] < 3800, negative
            specs[2], specs[3] = (0, "drop", 60), (0, "drop", 60)
            failed = await query(prefix + "both-timeout-budget", f"{prefix}both-timeout.google.test", qt)
            assert failed["rcode"] == 2 and 2800 < failed["ms"] < 3800, failed
        (root / "results.json").write_text(json.dumps(results, indent=2))
        print(f"PASS: {len(results)} local DoH scenarios")
    finally:
        if process is not None:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)
        if log is not None:
            log.close()
        for listener in listeners:
            listener.close()
            await listener.wait_closed()


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    asyncio.run(main(pathlib.Path(sys.argv[1]).resolve(), json.loads(pathlib.Path(sys.argv[2]).read_text())))
