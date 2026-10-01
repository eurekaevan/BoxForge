// Optional loopback integration check. No TUN, remote DNS, or production credentials.
// Input is a real BoxForge-generated config from Fixtures/transport.yaml.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { createServer as createTcpServer, connect } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';

const run = promisify(execFile);
const [coreArgument, configArgument] = process.argv.slice(2);
if (!coreArgument || !configArgument) {
    throw new Error('Usage: node scripts/verify-transports.mjs <sing-box core> <generated config.json>');
}
const core = resolve(coreArgument);
const revision = 'b609f959f57ce34416c51c7b87ce4a76f2e1df56';
const { stdout: version } = await run(core, ['version']);
assert.ok(version.includes('1.15.0-alpha.8') && version.includes(revision), version);
assert.ok(!/\bwith_grpc\b/.test(version), 'The qualified official build uses grpc-lite, not with_grpc');
console.log(version.trim());
const generated = JSON.parse(await readFile(resolve(configArgument), 'utf8'));
const directory = await mkdtemp(join(tmpdir(), 'boxforge-loopback-'));
const certificate = join(directory, 'certificate.pem');
const key = join(directory, 'key.pem');
const body = 'BoxForge loopback transport verified\n';
const httpServer = createServer((_request, response) => response.end(body));

async function freePort() {
    const server = createTcpServer();
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const port = server.address().port;
    await new Promise((resolveClose, reject) => server.close(error => error ? reject(error) : resolveClose()));
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
        const ready = await new Promise(resolveReady => {
            const socket = connect({ host: '127.0.0.1', port });
            socket.once('connect', () => { socket.destroy(); resolveReady(true); });
            socket.once('error', () => resolveReady(false));
            socket.setTimeout(500, () => { socket.destroy(); resolveReady(false); });
        });
        if (ready) return;
        await new Promise(resolveWait => setTimeout(resolveWait, 50));
    }
    throw new Error(`Listener ${port} did not start:\n${state.log}`);
}

async function stop(state) {
    if (!state || state.child.exitCode !== null || state.child.signalCode !== null || state.error) return;
    const exited = once(state.child, 'exit');
    state.child.kill('SIGTERM');
    const timer = setTimeout(() => state.child.kill('SIGKILL'), 2000);
    try { await exited; } finally { clearTimeout(timer); }
}

try {
    await run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
        '-keyout', key, '-out', certificate, '-subj', '/CN=node.example.com',
        '-addext', 'subjectAltName=DNS:node.example.com,DNS:ws.example.com']);
    httpServer.listen(0, '127.0.0.1');
    await once(httpServer, 'listening');
    const targetPort = httpServer.address().port;

    for (const tag of ['vless-ws', 'vless-grpc', 'trojan-ws', 'vless-httpupgrade', 'trojan-httpupgrade']) {
        const outbound = structuredClone(generated.outbounds.find(item => item.tag === tag));
        assert.ok(outbound?.transport, `Missing generated transport: ${tag}`);
        const serverPort = await freePort();
        const clientPort = await freePort();
        // Only replace the dial address and trust this temporary certificate.
        // Transport, ALPN, SNI, credentials and tuning come from BoxForge output.
        outbound.server = '127.0.0.1';
        outbound.server_port = serverPort;
        outbound.tls.certificate_path = certificate;
        const transport = structuredClone(outbound.transport);
        delete transport.headers; // Server response headers are not client request headers.
        const serverConfig = {
            log: { level: 'error' },
            inbounds: [{ type: outbound.type, tag: 'server', listen: '127.0.0.1', listen_port: serverPort,
                users: [outbound.type === 'vless' ? { uuid: outbound.uuid } : { password: outbound.password }],
                tls: { enabled: true, certificate_path: certificate, key_path: key }, transport }],
            outbounds: [{ type: 'direct', tag: 'direct' }], route: { final: 'direct' }
        };
        const clientConfig = {
            log: { level: 'error' },
            inbounds: [{ type: 'mixed', tag: 'mixed', listen: '127.0.0.1', listen_port: clientPort }],
            outbounds: [outbound], route: { final: tag }
        };
        const serverPath = join(directory, `${tag}-server.json`);
        const clientPath = join(directory, `${tag}-client.json`);
        await writeFile(serverPath, JSON.stringify(serverConfig));
        await writeFile(clientPath, JSON.stringify(clientConfig));
        await run(core, ['check', '-c', serverPath]);
        await run(core, ['check', '-c', clientPath]);
        let server;
        let client;
        try {
            server = launch(serverPath);
            await waitListening(server, serverPort);
            client = launch(clientPath);
            await waitListening(client, clientPort);
            const { stdout } = await run('curl', ['--fail', '--silent', '--show-error', '--max-time', '10',
                '--noproxy', '', '--socks5-hostname', `127.0.0.1:${clientPort}`, `http://127.0.0.1:${targetPort}/`],
                { timeout: 15000 });
            assert.equal(stdout, body);
            console.log(`PASS ${tag}: verified HTTP response through generated ${outbound.transport.type} outbound`);
        } catch (error) {
            throw new Error(`${tag}: ${error.message}\nserver:\n${server?.log ?? ''}\nclient:\n${client?.log ?? ''}`, { cause: error });
        } finally {
            await Promise.all([stop(client), stop(server)]);
        }
    }
} finally {
    await new Promise(resolveClose => httpServer.close(resolveClose));
    await rm(directory, { recursive: true, force: true });
}
