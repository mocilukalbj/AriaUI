import assert from 'node:assert/strict';
import fs from 'node:fs';
const moduleFrom = async file => import('data:text/javascript;base64,' + fs.readFileSync(new URL(file, import.meta.url)).toString('base64'));
const { AriaUINativeClient } = await moduleFrom('../undone_plugin/js/ariaui_native.js');
const { HandoffManager, deriveLabel } = await moduleFrom('../undone_plugin/js/handoff.js');
const stored = {};
globalThis.chrome = { runtime: { id: 'test-extension' }, storage: { local: {
    get: async () => structuredClone(stored),
    set: async values => Object.assign(stored, structuredClone(values)),
    remove: async key => { delete stored[key]; }
} } };

const client = new AriaUINativeClient();
client.port = { disconnect() {} };
client.instanceId = 'old';
let reconnects = 0, submissions = 0, intents = 0;
client.ensureConnected = async () => {
    reconnects++;
    client.port = { disconnect() {} };
    return client.instanceId = 'current';
};
client.exchange = async message => {
    if (message.action === 'Handshake') {
        if (client.instanceId === 'old') throw new Error('IdleConnectionClosed');
        return { status: 'Success', instanceId: 'current' };
    }
    submissions++;
    return { status: 'Success', gid: '1234567890abcdef' };
};
await client.addDownload('request', { url: 'https://example.com/file' }, async instance => {
    assert.equal(instance, 'current'); intents++;
});
assert.equal(reconnects, 1);
assert.equal(submissions, 1);
assert.equal(intents, 1);
client.instanceId = 'old';
assert.equal((await client.diagnose()).instanceId, 'current');
assert.equal(reconnects, 2, 'Diagnosis must probe the cached connection');
client.exchange = async message => {
    if (message.action === 'Handshake') return { status: 'Success', instanceId: 'current' };
    submissions++;
    throw new Error('LostAddResponse');
};
await assert.rejects(client.addDownload('lost', {}, async () => {}));
assert.equal(submissions, 2, 'An uncertain AddDownload must never be retried');

let notifications = 0;
const manager = new HandoffManager({ queryResult: async () => ({ status: 'Pending' }) }, async () => notifications++);
const record = { version: 1, requestId: 'pending', stage: 'submitting', instanceId: 'current', created: Date.now(), attempts: 0 };
await manager.outcome(record, { status: 'Pending' });
for (let i = 0; i < 3; i++) await manager.recoverOne(record);
assert.equal(notifications, 1);
assert.equal(record.stage, 'unknown');
const restarted = new HandoffManager({}, async () => notifications++);
await restarted.load();
await restarted.attention(restarted.records.get('pending'), 'UnknownOutcome');
assert.equal(notifications, 1, 'Worker restart must not repeat an alert');
await restarted.outcome({ version: 1, requestId: 'another', created: Date.now() }, { status: 'Disconnected' });
assert.equal(notifications, 2, 'A different unresolved download still needs an alert');

// deriveLabel tests
assert.equal(deriveLabel({ out: 'custom.iso', url: 'https://example.com/file' }), 'custom.iso');
assert.equal(deriveLabel({ url: 'https://example.com/downloads/ubuntu-22.04.iso' }), 'ubuntu-22.04.iso');
assert.equal(deriveLabel({ url: 'https://example.com/downloads/ubuntu-22.04.iso/' }), 'ubuntu-22.04.iso', 'Trailing slash should not hide filename');
assert.equal(deriveLabel({ url: 'https://example.com/download?id=123' }), 'download');
assert.equal(deriveLabel({ url: 'https://example.com/' }), 'example.com');
assert.equal(deriveLabel({ url: 'magnet:?xt=urn:btih:1234567890abcdef&dn=Fedora-Workstation' }), 'Fedora-Workstation');
assert.equal(deriveLabel(null), '手动发送');

// manager.create label derivation
const itemWithoutFilename = { id: 99, url: 'https://example.com/archive.zip', startTime: new Date().toISOString() };
const recCreated = await manager.create(itemWithoutFilename);
assert.equal(recCreated.label, 'archive.zip', 'create() should derive label from item URL when filename is absent');

console.log('PASS idle reconnect, live diagnosis, no add replay, durable alert deduplication, deriveLabel');
