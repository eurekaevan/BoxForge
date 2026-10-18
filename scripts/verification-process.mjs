import { spawn } from 'node:child_process';
import { createSocket } from 'node:dgram';
import { once } from 'node:events';
import { createServer, connect } from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';

// A candidate, not a reservation: the core must bind it after this socket closes.
export async function freePort(protocol = 'tcp') {
    if (!['tcp', 'udp'].includes(protocol)) throw new Error(`Unsupported protocol: ${protocol}`);
    const listener = protocol === 'udp' ? createSocket('udp4') : createServer();
    if (protocol === 'udp') listener.bind(0, '127.0.0.1');
    else listener.listen(0, '127.0.0.1');
    await once(listener, 'listening');
    const port = listener.address().port;
    await new Promise((done, reject) => listener.close(error => error ? reject(error) : done()));
    return port;
}

export function launch(command, args) {
    const child = spawn(command, args, { stdio: ['ignore', 'pipe', 'pipe'] });
    const state = { child, log: '', error: null };
    child.stdout.on('data', data => { state.log += data; });
    child.stderr.on('data', data => { state.log += data; });
    child.on('error', error => { state.error = error; });
    return state;
}

export async function waitListening(state, port) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
        if (state.error) throw state.error;
        if (state.child.exitCode !== null || state.child.signalCode !== null)
            throw new Error(`Process exited before listener ${port} started:\n${state.log}`);
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

export async function stop(state) {
    if (!state || state.error || state.child.exitCode !== null || state.child.signalCode !== null) return;
    const exited = once(state.child, 'exit');
    state.child.kill('SIGTERM');
    const timer = setTimeout(() => state.child.kill('SIGKILL'), 2000);
    try { await exited; } finally { clearTimeout(timer); }
}
