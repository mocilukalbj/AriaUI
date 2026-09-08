const PREFIX = 'handoff:';
const NOT_ACCEPTED = new Set(['BadRequest', 'UnsupportedScheme', 'InvalidPath', 'HeaderInjection',
    'RateLimited', 'QueueFull', 'UnsupportedVersion', 'UnsupportedAction', 'InstanceMismatch']);
export const validGid = gid => typeof gid === 'string' && /^[0-9a-f]{16}$/i.test(gid) && !/^0{16}$/.test(gid);

export class HandoffManager {
    constructor(client, notify = async () => {}) {
        this.client = client;
        this.notify = notify;
        this.records = new Map();
        this.busy = new Set();
    }
    async load() {
        const values = await chrome.storage.local.get(null);
        for (const [key, value] of Object.entries(values)) {
            if (key.startsWith(PREFIX) && value?.version === 1 && key === PREFIX + value.requestId) {
                this.records.set(value.requestId, value);
            }
        }
        await this.prune();
    }
    async prune() {
        const done = [...this.records.values()].filter(r => r.stage === 'done').sort((a, b) => b.updated - a.updated);
        for (const record of done.slice(50)) {
            await chrome.storage.local.remove(PREFIX + record.requestId);
            this.records.delete(record.requestId);
        }
    }
    async save(record, update = {}) {
        const next = { ...record, ...update, updated: Date.now() };
        await chrome.storage.local.set({ [PREFIX + record.requestId]: next });
        Object.assign(record, next);
        this.records.set(record.requestId, record);
    }
    async create(item) {
        if ([...this.records.values()].filter(r => r.stage !== 'done').length >= 100) throw new Error('TooManyUnresolved');
        const record = { version: 1, requestId: crypto.randomUUID(), stage: 'prepared',
            created: Date.now(), attempts: 0 };
        if (item) {
            record.downloadId = item.id;
            record.startTime = item.startTime;
            record.label = (item.filename || '').split(/[\\/]/).pop().slice(0, 160);
        } else record.label = '手动发送';
        await this.save(record);
        return record;
    }
    async item(record) {
        if (record.downloadId === undefined) return null;
        const [item] = await chrome.downloads.search({ id: record.downloadId });
        return item?.startTime === record.startTime ? item : null;
    }
    async attention(record, reason) {
        await this.save(record, { stage: 'attention', reason });
        await this.notify(record);
    }
    async resume(record, reason) {
        // Durable rejection precedes the browser side effect, allowing crash recovery.
        await this.save(record, { stage: 'rejected', reason });
        if (record.downloadId !== undefined) {
            const item = await this.item(record);
            if (!item) return this.attention(record, 'BrowserItemMissing');
            if (item.state === 'in_progress' && item.paused) {
                try {
                    await chrome.downloads.resume(item.id);
                    if ((await this.item(record))?.paused) return this.attention(record, 'ResumeFailed');
                } catch { return this.attention(record, 'ResumeFailed'); }
            } else if (item.state === 'interrupted' && item.error !== 'USER_CANCELED') {
                return this.attention(record, 'ResumeInBrowser');
            }
        }
        await this.save(record, { stage: 'done', reason });
    }
    async finishAccepted(record) {
        if (record.downloadId !== undefined) {
            const item = await this.item(record);
            if (!item) return this.attention(record, 'AcceptedBrowserItemMissing');
            if (item.state === 'complete') return this.attention(record, 'BrowserAlreadyCompleted');
            if (item.state === 'in_progress' && !item.paused) return this.attention(record, 'BrowserWasResumed');
            if (item.state !== 'interrupted' || item.error !== 'USER_CANCELED') {
                try {
                    await chrome.downloads.cancel(item.id);
                    const after = await this.item(record);
                    if (!after || after.state !== 'interrupted' || after.error !== 'USER_CANCELED') {
                        return this.attention(record, 'CancelFailed');
                    }
                } catch { return this.attention(record, 'CancelFailed'); }
            }
        }
        await this.save(record, { stage: 'done', reason: 'Sent' });
    }
    async outcome(record, response, isQuery = false) {
        if (response?.status === 'Success' && validGid(response.gid)) {
            await this.save(record, { stage: 'accepted', gid: response.gid, reason: 'Sent' });
            return this.finishAccepted(record);
        }
        // Query errors describe the query, not whether the original add was accepted.
        // Failed in the existing gateway includes generic exceptions: treat as unknown.
        if (!isQuery && NOT_ACCEPTED.has(response?.status)) return this.resume(record, response.status);
        await this.save(record, { stage: 'unknown', reason: response?.status || 'UnknownOutcome' });
        await this.notify(record);
    }
    async send(record, payload) {
        try {
            const result = await this.client.addDownload(record.requestId, payload, async instanceId => {
                if (record.downloadId !== undefined) {
                    const item = await this.item(record);
                    if (!item || item.state !== 'in_progress' || !item.paused) throw new Error('BrowserChanged');
                }
                await this.save(record, { stage: 'submitting', instanceId });
            });
            await this.outcome(record, result);
        } catch {
            if (['prepared', 'paused'].includes(record.stage)) await this.resume(record, 'NotSubmitted');
            else if (record.stage === 'submitting') await this.outcome(record, { status: 'Disconnected' });
            else throw new Error('HandoffNeedsAttention');
        }
    }
    async capture(item, payload, releaseFilename) {
        if ([...this.records.values()].some(r => r.downloadId === item.id && r.startTime === item.startTime)) return;
        const record = await this.create(item);
        this.busy.add(record.requestId);
        try {
            const current = await this.item(record);
            if (!current || current.paused || current.state !== 'in_progress') {
                await this.save(record, { stage: 'done', reason: 'NotSubmitted' });
                return;
            }
            try { await chrome.downloads.pause(item.id); }
            catch { await this.resume(record, 'PauseFailed'); return; }
            await this.save(record, { stage: 'paused' });
            releaseFilename();
            const paused = await this.item(record);
            if (!paused?.paused || paused.state !== 'in_progress') {
                await this.resume(record, 'PauseFailed');
                return;
            }
            await this.send(record, payload);
        } finally { this.busy.delete(record.requestId); }
    }
    async manual(payload) {
        const record = await this.create();
        this.busy.add(record.requestId);
        try { await this.send(record, payload); return record; }
        finally { this.busy.delete(record.requestId); }
    }
    async recoverOne(record, force = false) {
        if (this.busy.has(record.requestId) || record.stage === 'done') return;
        this.busy.add(record.requestId);
        try {
            if (['prepared', 'paused', 'rejected'].includes(record.stage)) {
                return await this.resume(record, record.reason || 'NotSubmitted');
            }
            if (record.stage === 'accepted') return await this.finishAccepted(record);
            if (record.gid) {
                if (force) return await this.finishAccepted(record);
                return;
            }
            if (!record.instanceId || (!force && (record.stage === 'attention' || record.attempts >= 3))) return;
            if (!force && Date.now() - record.created > 600000) {
                await this.outcome(record, { status: 'UnknownOutcome' }, true);
                await this.save(record, { attempts: 3 });
                return;
            }
            await this.save(record, { attempts: record.attempts + 1 });
            let response;
            try { response = await this.client.queryResult(record); }
            catch { response = { status: 'Disconnected' }; }
            await this.outcome(record, response, true);
            if (['InstanceMismatch', 'UnknownOutcome'].includes(response.status)) await this.save(record, { attempts: 3 });
        } finally { this.busy.delete(record.requestId); }
    }
    async recover() {
        for (const record of this.records.values()) await this.recoverOne(record);
        await this.prune();
    }
    async resolve(requestId, action) {
        const record = this.records.get(requestId);
        if (!record || this.busy.has(requestId)) throw new Error('BusyOrMissing');
        if (action === 'query') return this.recoverOne(record, true);
        this.busy.add(requestId);
        try {
            // Explicit user decisions only; never re-add a native task here.
            if (action === 'resume') await this.resume(record, 'UserContinuedBrowser');
            else if (action === 'dismiss') await this.save(record, { stage: 'done', reason: 'UserResolved' });
            else throw new Error('UnknownAction');
        } finally { this.busy.delete(requestId); }
    }
}
