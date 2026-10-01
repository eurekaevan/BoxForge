// Real generated leaves, local HTTP target, owned temporary processes and certificate.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { createServer as createTcpServer, connect } from 'node:net';
import { createSocket } from 'node:dgram';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';

const run = promisify(execFile);
const [coreArgument, configArgument, selection = 'all'] = process.argv.slice(2);
assert.ok(coreArgument && configArgument && ['hysteria2', 'anytls', 'all'].includes(selection),
    'Usage: node scripts/verify-session-protocols.mjs <core> <generated config.json> [hysteria2|anytls|all]');
const core = resolve(coreArgument);
const { stdout: version } = await run(core, ['version']);
assert.ok(version.includes('1.15.0-alpha.8') && version.includes('b609f959f57ce34416c51c7b87ce4a76f2e1df56'), version);
const generated = JSON.parse(await readFile(resolve(configArgument), 'utf8'));
const tags = selection === 'anytls' ? ['anytls-session'] : ['hy2-default', 'hy2-bandwidth', 'hy2-gecko', 'hy2-hop'];
if (selection === 'all') tags.push('anytls-session');
const directory = await mkdtemp(join(tmpdir(), 'boxforge-session-protocols-'));
const certificate = join(directory, 'certificate.pem');
const key = join(directory, 'key.pem');
const body = 'BoxForge session protocol verified\n';
const httpServer = createServer((_request, response) => response.end(body));
const delay = milliseconds => new Promise(done => setTimeout(done, milliseconds));

async function freePort() {
    const listener = createTcpServer();
    listener.listen(0, '127.0.0.1');
    await once(listener, 'listening');
    const port = listener.address().port;
    await new Promise((done, reject) => listener.close(error => error ? reject(error) : done()));
    return port;
}

function launch(path) {
    const child = spawn(core, ['run', '-c', path], { stdio: ['ignore', 'pipe', 'pipe'] });
    const state = { child, log: '', error: null };
    child.stdout.on('data', data => { state.log += data; });
    child.stderr.on('data', data => { state.log += data; });
    child.on('error', error => { state.error = error; });
    return state;
}

async function waitListening(state, port) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
        if (state.error) throw state.error;
        if (state.child.exitCode !== null) throw new Error(state.log);
        const ready = await new Promise(done => {
            const socket = connect({ host: '127.0.0.1', port });
            socket.once('connect', () => { socket.destroy(); done(true); });
            socket.once('error', () => done(false));
            socket.setTimeout(500, () => { socket.destroy(); done(false); });
        });
        if (ready) return;
        await delay(50);
    }
    throw new Error(`Listener ${port} did not start:\n${state.log}`);
}

async function stop(state) {
    if (!state || state.error || state.child.exitCode !== null || state.child.signalCode !== null) return;
    const exited = once(state.child, 'exit');
    state.child.kill('SIGTERM');
    const timer = setTimeout(() => state.child.kill('SIGKILL'), 2000);
    try { await exited; } finally { clearTimeout(timer); }
}

// Both hopping ports forward to one server, preserving its QUIC connection state.
async function hopRelay(backendPort) {
    const sockets = [];
    const peers = new Map();
    const counts = [0, 0];
    let failure;
    try {
        for (let index = 0; index < 2; index++) {
            const listener = createSocket('udp4');
            sockets.push(listener);
            listener.on('error', error => { failure = error; });
            listener.bind(0, '127.0.0.1');
            await once(listener, 'listening');
            listener.on('message', (data, peer) => {
                counts[index]++;
                const identity = `${index}:${peer.address}:${peer.port}`;
                let upstream = peers.get(identity);
                if (!upstream) {
                    upstream = createSocket('udp4');
                    peers.set(identity, upstream);
                    upstream.on('error', error => { failure = error; });
                    upstream.on('message', response => listener.send(response, peer.port, peer.address));
                }
                upstream.send(data, backendPort, '127.0.0.1');
            });
        }
        return { ports: sockets.map(socket => socket.address().port), counts,
            peerCount: () => peers.size,
            check: () => { if (failure) throw failure; },
            close: () => { for (const socket of [...sockets, ...peers.values()]) socket.close(); } };
    } catch (error) {
        for (const socket of sockets) socket.close();
        throw error;
    }
}

try {
    await run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
        '-keyout', key, '-out', certificate, '-subj', '/CN=node.example.com',
        '-addext', 'subjectAltName=DNS:node.example.com']);
    httpServer.listen(0, '127.0.0.1');
    await once(httpServer, 'listening');
    for (const tag of tags) {
        const outbound = structuredClone(generated.outbounds.find(item => item.tag === tag));
        assert.ok(outbound && ['hysteria2', 'anytls'].includes(outbound.type), `Missing generated leaf: ${tag}`);
        if (tag === 'hy2-bandwidth') {
            assert.equal(outbound.up_mbps, 100);
            assert.equal(outbound.down_mbps, 80);
        }
        if (tag === 'hy2-gecko') {
            assert.equal(outbound.obfs?.type, 'gecko');
            assert.equal(outbound.obfs.min_packet_size, 600);
            assert.equal(outbound.obfs.max_packet_size, 1200);
        }
        if (tag === 'anytls-session') {
            assert.equal(outbound.idle_session_check_interval, '30s');
            assert.equal(outbound.idle_session_timeout, '30s');
            assert.equal(outbound.min_idle_session, 1);
            assert.equal(outbound.client_metadata, 'test-only');
        }
        const serverPort = await freePort();
        const clientPort = await freePort();
        const readinessPort = await freePort();
        let relay;
        let server;
        let client;
        try {
            outbound.server = '127.0.0.1';
            outbound.tls.certificate_path = certificate;
            if (tag === 'hy2-hop') {
                assert.equal(outbound.hop_interval, '20s');
                assert.deepEqual(outbound.server_ports, ['40000:40001']);
                relay = await hopRelay(serverPort);
                outbound.server_ports = relay.ports.map(port => `${port}:${port}`);
                delete outbound.server_port;
            } else outbound.server_port = serverPort;
            const inbound = { type: outbound.type, tag: 'server', listen: '127.0.0.1', listen_port: serverPort,
                users: [{ password: outbound.password }],
                tls: { enabled: true, certificate_path: certificate, key_path: key } };
            if (outbound.obfs) inbound.obfs = structuredClone(outbound.obfs);
            const serverConfig = { log: { level: 'error' },
                inbounds: [inbound, { type: 'mixed', tag: 'ready', listen: '127.0.0.1', listen_port: readinessPort }],
                outbounds: [{ type: 'direct', tag: 'direct' }], route: { final: 'direct' } };
            const clientConfig = { log: { level: 'error' },
                inbounds: [{ type: 'mixed', tag: 'mixed', listen: '127.0.0.1', listen_port: clientPort }],
                outbounds: [outbound], route: { final: tag } };
            const serverPath = join(directory, `${tag}-server.json`);
            const clientPath = join(directory, `${tag}-client.json`);
            await writeFile(serverPath, JSON.stringify(serverConfig));
            await writeFile(clientPath, JSON.stringify(clientConfig));
            await run(core, ['check', '-c', serverPath]);
            await run(core, ['check', '-c', clientPath]);
            server = launch(serverPath);
            await waitListening(server, readinessPort);
            client = launch(clientPath);
            await waitListening(client, clientPort);
            const request = async () => {
                const { stdout } = await run('curl', ['--fail', '--silent', '--show-error', '--max-time', '10',
                    '--noproxy', '', '--socks5-hostname', `127.0.0.1:${clientPort}`,
                    `http://127.0.0.1:${httpServer.address().port}/`], { timeout: 15000 });
                assert.equal(stdout, body);
                relay?.check();
            };
            await request();
            if (relay) {
                const initialPeers = relay.peerCount();
                await delay(22000);
                await request();
                // A hop can randomly choose the same destination port. The old
                // socket remains open, so a new peer proves socket rotation.
                assert.ok(relay.peerCount() > initialPeers, 'Hopping did not rotate its UDP source socket');
            }
            console.log(`PASS ${tag}: HTTP response through generated ${outbound.type}${relay
                ? `; UDP source socket rotated, destination port counts ${relay.counts}` : ''}`);
        } catch (error) {
            throw new Error(`FAILED ${tag}: ${error.message}\nserver:\n${server?.log ?? ''}\nclient:\n${client?.log ?? ''}`, { cause: error });
        } finally {
            await Promise.all([stop(client), stop(server)]);
            relay?.close();
        }
    }
} finally {
    await new Promise(done => httpServer.close(done));
    await rm(directory, { recursive: true, force: true });
}
