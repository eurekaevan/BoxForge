// Optional loopback verification against real generated fixture leaves.
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
const [coreArgument, configArgument] = process.argv.slice(2);
if (!coreArgument || !configArgument)
    throw new Error('Usage: node scripts/verify-shadowsocks.mjs <sing-box core> <generated config.json>');
const core = resolve(coreArgument);
const { stdout: version } = await run(core, ['version']);
assert.ok(version.includes('1.15.0-alpha.8')
    && version.includes('b609f959f57ce34416c51c7b87ce4a76f2e1df56'), version);
const generated = JSON.parse(await readFile(resolve(configArgument), 'utf8'));
const directory = await mkdtemp(join(tmpdir(), 'boxforge-shadowsocks-'));
const body = 'BoxForge Shadowsocks loopback verified\n';
const httpServer = createServer((_request, response) => {
    response.setHeader('Content-Length', Buffer.byteLength(body));
    response.end(body);
});
const echo = createSocket('udp4');
echo.on('message', (data, peer) => echo.send(data, peer.port, peer.address));

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
        await new Promise(done => setTimeout(done, 50));
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

// Keep a buffered reader for TCP replies; SOCKS frames may arrive fragmented.
function reader(socket) {
    let pending = Buffer.alloc(0);
    let wake;
    let failure;
    socket.on('data', data => { pending = Buffer.concat([pending, data]); wake?.(); });
    socket.on('error', error => { failure = error; wake?.(); });
    socket.on('close', () => { failure ??= new Error('SOCKS control connection closed'); wake?.(); });
    return async length => {
        while (pending.length < length) {
            if (failure) throw failure;
            await new Promise(done => { wake = done; });
        }
        const result = pending.subarray(0, length);
        pending = pending.subarray(length);
        return result;
    };
}

async function verifyTcp(clientPort, targetPort) {
    const socket = connect({ host: '127.0.0.1', port: clientPort });
    const read = reader(socket);
    const timer = setTimeout(() => socket.destroy(new Error('SOCKS TCP timeout')), 10000);
    try {
        await once(socket, 'connect');
        socket.write(Buffer.from([5, 1, 0]));
        assert.deepEqual(await read(2), Buffer.from([5, 0]));
        const request = Buffer.from([5, 1, 0, 1, 127, 0, 0, 1, 0, 0]);
        request.writeUInt16BE(targetPort, 8);
        socket.write(request);
        assert.deepEqual(await read(4), Buffer.from([5, 0, 0, 1]));
        await read(6);
        socket.write(`GET / HTTP/1.1\r\nHost: 127.0.0.1:${targetPort}\r\nConnection: close\r\n\r\n`);
        let headers = '';
        while (!headers.endsWith('\r\n\r\n')) {
            assert.ok(headers.length < 8192, 'Unexpected HTTP header size');
            headers += (await read(1)).toString('ascii');
        }
        assert.match(headers, /^HTTP\/1\.1 200 /);
        assert.match(headers, new RegExp(`\\r\\nContent-Length: ${Buffer.byteLength(body)}\\r\\n`, 'i'));
        assert.equal((await read(Buffer.byteLength(body))).toString('utf8'), body);
    } finally {
        clearTimeout(timer);
        socket.destroy();
    }
}

async function verifyUdp(clientPort, targetPort, tag) {
    const control = connect({ host: '127.0.0.1', port: clientPort });
    const read = reader(control);
    const udp = createSocket('udp4');
    const timer = setTimeout(() => control.destroy(new Error('SOCKS UDP timeout')), 10000);
    try {
        await once(control, 'connect');
        control.write(Buffer.from([5, 1, 0]));
        assert.deepEqual(await read(2), Buffer.from([5, 0]));
        udp.bind(0, '127.0.0.1');
        await once(udp, 'listening');
        const request = Buffer.from([5, 3, 0, 1, 127, 0, 0, 1, 0, 0]);
        request.writeUInt16BE(udp.address().port, 8);
        control.write(request);
        const reply = await read(4);
        assert.deepEqual(reply, Buffer.from([5, 0, 0, 1]), 'Expected an IPv4 SOCKS UDP relay');
        const endpoint = await read(6);
        const relayPort = endpoint.readUInt16BE(4);
        const payload = Buffer.from(`BoxForge ${tag} datagram`);
        const packet = Buffer.alloc(10 + payload.length);
        packet.set([0, 0, 0, 1, 127, 0, 0, 1]);
        packet.writeUInt16BE(targetPort, 8);
        payload.copy(packet, 10);
        const response = Promise.race([
            once(udp, 'message'),
            once(control, 'error').then(([error]) => { throw error; }),
            once(control, 'close').then(() => { throw new Error('SOCKS UDP control connection closed'); })
        ]);
        udp.send(packet, relayPort, '127.0.0.1');
        const [received] = await response;
        assert.equal(received.readUInt16BE(0), 0);
        assert.equal(received[2], 0, 'SOCKS fragmentation must be absent');
        assert.equal(received[3], 1);
        assert.equal(received.subarray(4, 8).toString('hex'), '7f000001');
        assert.equal(received.readUInt16BE(8), targetPort);
        assert.deepEqual(received.subarray(10), payload);
    } finally {
        clearTimeout(timer);
        control.destroy();
        try { udp.close(); } catch (error) { if (error.code !== 'ERR_SOCKET_DGRAM_NOT_RUNNING') throw error; }
    }
}

try {
    httpServer.listen(0, '127.0.0.1');
    await once(httpServer, 'listening');
    echo.bind(0, '127.0.0.1');
    await once(echo, 'listening');
    for (const tag of ['ss-plain', 'ss-uot-v1', 'ss-uot-v2', 'ss-obfs-http', 'ss-obfs-tls', 'ss-v2ray-ws', 'ss-v2ray-ws-tls']) {
        const outbound = structuredClone(generated.outbounds.find(item => item.tag === tag));
        assert.equal(outbound?.type, 'shadowsocks', `Missing generated Shadowsocks leaf: ${tag}`);
        const serverPort = await freePort();
        const clientPort = await freePort();
        outbound.server = '127.0.0.1';
        outbound.server_port = serverPort;
        const clientConfig = { log: { level: 'error' },
            inbounds: [{ type: 'mixed', tag: 'mixed', listen: '127.0.0.1', listen_port: clientPort }],
            outbounds: [outbound], route: { final: tag } };
        const clientPath = join(directory, `${tag}-client.json`);
        await writeFile(clientPath, JSON.stringify(clientConfig));
        await run(core, ['check', '-c', clientPath]);
        if (outbound.plugin) {
            console.log(`CHECK-ONLY ${tag}: generated plugin accepted; target inbound has no plugin server support`);
            continue;
        }
        const expectedVersion = tag === 'ss-uot-v1' ? 1 : tag === 'ss-uot-v2' ? 2 : null;
        if (expectedVersion !== null) {
            assert.equal(outbound.udp_over_tcp?.enabled, true);
            assert.equal(outbound.udp_over_tcp.version, expectedVersion);
        }
        const serverConfig = { log: { level: 'error' },
            inbounds: [{ type: 'shadowsocks', tag: 'server', listen: '127.0.0.1', listen_port: serverPort,
                method: outbound.method, password: outbound.password, network: 'tcp' }],
            outbounds: [{ type: 'direct', tag: 'direct' }], route: { final: 'direct' } };
        const serverPath = join(directory, `${tag}-server.json`);
        await writeFile(serverPath, JSON.stringify(serverConfig));
        await run(core, ['check', '-c', serverPath]);
        let server;
        let client;
        try {
            server = launch(serverPath);
            await waitListening(server, serverPort);
            client = launch(clientPath);
            await waitListening(client, clientPort);
            if (expectedVersion !== null) {
                await verifyUdp(clientPort, echo.address().port, tag);
                console.log(`PASS ${tag}: echoed UDP payload through generated UoT v${expectedVersion}; SS server is TCP-only`);
            } else {
                await verifyTcp(clientPort, httpServer.address().port);
                console.log(`PASS ${tag}: HTTP response through generated Shadowsocks TCP outbound`);
            }
        } catch (error) {
            throw new Error(`${tag}: ${error.message}\nserver:\n${server?.log ?? ''}\nclient:\n${client?.log ?? ''}`, { cause: error });
        } finally {
            await Promise.all([stop(client), stop(server)]);
        }
    }
} finally {
    await new Promise(done => httpServer.close(done));
    try { echo.close(); } catch (error) { if (error.code !== 'ERR_SOCKET_DGRAM_NOT_RUNNING') throw error; }
    await rm(directory, { recursive: true, force: true });
}
