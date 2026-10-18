import assert from 'node:assert/strict';
import { createSocket } from 'node:dgram';
import { once } from 'node:events';
import { createServer } from 'node:net';
import test from 'node:test';
import { freePort, launch, waitListening, stop } from './verification-process.mjs';

for (const protocol of ['tcp', 'udp']) {
    test(`${protocol} port candidate is released for the next listener`, async () => {
        const port = await freePort(protocol);
        const listener = protocol === 'udp' ? createSocket('udp4') : createServer();
        if (protocol === 'udp') listener.bind(port, '127.0.0.1');
        else listener.listen(port, '127.0.0.1');
        await once(listener, 'listening');
        await new Promise(done => listener.close(done));
    });
}

test('starts, captures diagnostics, waits for readiness and stops a process', async () => {
    const port = await freePort();
    const state = launch(process.execPath, ['-e', `
        console.log('started');
        console.error('diagnostic');
        require('node:net').createServer(socket => socket.end()).listen(${port}, '127.0.0.1');
    `]);
    try {
        await waitListening(state, port);
        assert.match(state.log, /started/);
        assert.match(state.log, /diagnostic/);
    } finally {
        await stop(state);
    }
    assert.equal(state.child.signalCode, 'SIGTERM');
    await stop(state);
});

test('reports launch failure and cleanup does not hang', async () => {
    const state = launch('/nonexistent-boxforge-verification-executable', []);
    await assert.rejects(waitListening(state, await freePort()), { code: 'ENOENT' });
    await stop(state);
});

test('reports premature exit with diagnostics', async () => {
    const state = launch(process.execPath, ['-e', "console.error('startup failed'); process.exitCode = 7;"]);
    try {
        await assert.rejects(waitListening(state, await freePort()), /startup failed/);
        assert.equal(state.child.exitCode, 7);
    } finally {
        await stop(state);
    }
});

test('forces termination when a process ignores SIGTERM', async () => {
    const port = await freePort();
    const state = launch(process.execPath, ['-e', `
        process.on('SIGTERM', () => {});
        require('node:net').createServer(socket => socket.end()).listen(${port}, '127.0.0.1');
    `]);
    try {
        await waitListening(state, port);
    } finally {
        await stop(state);
    }
    assert.equal(state.child.signalCode, 'SIGKILL');
});
