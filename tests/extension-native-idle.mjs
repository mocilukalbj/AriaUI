// Windows integration: node tests/extension-native-idle.mjs [AOT publish directory]
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
const published = path.resolve(process.argv[2] || fileURLToPath(new URL('../publish/win-x64-aot', import.meta.url)));
const host = path.join(published, 'AriaUI.Host.exe');
const app = path.join(published, 'AriaUI.exe');
assert.ok(fs.existsSync(host) && fs.existsSync(app));
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ariaui-idle-'));
const downloads = path.join(root, 'downloads');
fs.mkdirSync(downloads);
fs.writeFileSync(path.join(root, 'config.json'), JSON.stringify({ DefaultDownloadDir: downloads, EnableBtTrackers: false }));
const env = { ...process.env, ARIAUI_DATA_DIR: root, ARIAUI_APP_PATH: app };
delete env.ARIAUI_LOCK_PATH;
delete env.ARIAUI_SOCKET_PATH;
delete env.ARIAUI_NATIVE_DIR;
const application = spawn(app, [], { env, windowsHide: true, stdio: 'ignore', cwd: published });
const children = [];
let connections = 0, adds = 0;
const data = Buffer.alloc(16384, 65);
const server = http.createServer((req, res) => { res.writeHead(200, { 'Content-Length': data.length }); res.end(data); });
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
globalThis.chrome = { runtime: { id: 'ehicmejmhlcflckpndnpmedmfkopcdfb', connectNative() {
    connections++;
    const child = spawn(host, [], { env, windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'], cwd: published });
    children.push(child);
    const messages = [], disconnects = [];
    let buffered = Buffer.alloc(0);
    child.stdin.on('error', () => {});
    child.on('close', () => disconnects.forEach(fn => fn()));
    child.stdout.on('data', chunk => {
        buffered = Buffer.concat([buffered, chunk]);
        while (buffered.length >= 4 && buffered.length >= 4 + buffered.readUInt32LE()) {
            const size = buffered.readUInt32LE();
            const response = JSON.parse(buffered.subarray(4, 4 + size).toString());
            buffered = buffered.subarray(4 + size);
            messages.forEach(fn => fn(response));
        }
    });
    return {
        onMessage: { addListener: fn => messages.push(fn) },
        onDisconnect: { addListener: fn => disconnects.push(fn) },
        disconnect() { child.stdin.end(); },
        postMessage(message) {
            if (message.action === 'AddDownload') adds++;
            const body = Buffer.from(JSON.stringify(message));
            const length = Buffer.alloc(4); length.writeUInt32LE(body.length);
            child.stdin.write(Buffer.concat([length, body]));
        }
    };
} } };
const code = fs.readFileSync(new URL('../undone_plugin/js/ariaui_native.js', import.meta.url));
const { AriaUINativeClient } = await import('data:text/javascript;base64,' + code.toString('base64'));
const client = new AriaUINativeClient();
try {
    await client.diagnose();
    await delay(11500); // Exceed the actual gateway's 10-second idle timeout.
    const result = await client.addDownload(crypto.randomUUID(), {
        url: `http://127.0.0.1:${server.address().port}/file.bin`, out: 'idle-test.bin'
    }, async () => {});
    assert.equal(result.status, 'Success');
    assert.equal(adds, 1, 'Only one add may be submitted after reconnecting');
    assert.ok(connections >= 2, 'The stale native connection must be replaced');
    const file = path.join(downloads, 'idle-test.bin');
    for (let i = 0; i < 100 && (!fs.existsSync(file) || fs.statSync(file).size !== data.length); i++) await delay(100);
    assert.deepEqual(fs.readFileSync(file), data);
    console.log('PASS real AOT gateway idle timeout -> reconnect -> exactly one download with matching content');
} finally {
    client.disconnect();
    for (const child of children) if (child.exitCode === null) child.kill();
    application.kill();
    server.closeAllConnections(); server.close();
    await delay(500);
    fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
