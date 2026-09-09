// One outstanding frame per connection, matching the existing Linux thin host.
export class AriaUINativeClient {
    constructor(hostName = 'com.ariaui.downloader', timeout = 12000) {
        this.hostName = hostName;
        this.timeout = timeout;
        this.port = null;
        this.instanceId = null;
        this.connecting = null;
        this.pending = null;
        this.tail = Promise.resolve();
        this.queued = 0;
    }

    serial(work) {
        if (this.queued >= 32) return Promise.reject(new Error('LocalQueueFull'));
        this.queued++;
        const result = this.tail.then(work);
        this.tail = result.catch(() => {}).finally(() => this.queued--);
        return result;
    }

    disconnect(reason = 'Disconnected') {
        const port = this.port;
        this.port = null;
        this.instanceId = null;
        const pending = this.pending;
        this.pending = null;
        pending?.reject(new Error(reason));
        try { port?.disconnect(); } catch { /* Already disconnected. */ }
    }

    async ensureConnected() {
        if (this.connecting) return this.connecting;
        if (this.port && this.instanceId) return this.instanceId;
        this.connecting = (async () => {
            const port = chrome.runtime.connectNative(this.hostName);
            this.port = port;
            port.onMessage.addListener(message => {
                if (this.port !== port) return;
                const pending = this.pending;
                if (!pending) { this.disconnect('UnexpectedResponse'); return; }
                if (message?.version !== 1 || typeof message.status !== 'string' ||
                    (pending.requestId && message.requestId !== pending.requestId)) {
                    this.disconnect('InvalidResponse');
                    return;
                }
                this.pending = null;
                pending.resolve(message);
            });
            port.onDisconnect.addListener(() => {
                void chrome?.runtime?.lastError;
                if (this.port === port) this.disconnect();
            });
            const response = await this.exchange({ version: 1, action: 'Handshake' });
            if (response.status !== 'Success' || typeof response.instanceId !== 'string' || !response.instanceId) {
                this.disconnect('HandshakeRejected');
                throw new Error('HandshakeRejected');
            }
            this.instanceId = response.instanceId;
            return this.instanceId;
        })();
        try { return await this.connecting; }
        finally { this.connecting = null; }
    }

    exchange(message) {
        if (!this.port || this.pending) return Promise.reject(new Error('ConnectionBusy'));
        if (new TextEncoder().encode(JSON.stringify(message)).length > 65536) return Promise.reject(new Error('FrameTooLarge'));
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => this.disconnect('Timeout'), this.timeout);
            this.pending = {
                requestId: message.requestId,
                resolve: value => { clearTimeout(timer); resolve(value); },
                reject: error => { clearTimeout(timer); reject(error); }
            };
            try { this.port.postMessage(message); }
            catch { this.disconnect('SendFailed'); }
        });
    }

    async liveConnection() {
        // A native port can outlive the gateway's idle timeout. Probe before
        // recording submission intent; retry only this side-effect-free handshake.
        for (let attempt = 0; attempt < 2; attempt++) {
            try {
                if (!this.port || !this.instanceId) return await this.ensureConnected();
                const response = await this.exchange({ version: 1, action: 'Handshake' });
                if (response.status !== 'Success' || typeof response.instanceId !== 'string' || !response.instanceId) {
                    throw new Error('HandshakeRejected');
                }
                this.instanceId = response.instanceId;
                return this.instanceId;
            } catch (error) {
                this.disconnect();
                if (attempt === 1) throw error;
            }
        }
    }

    diagnose() { return this.serial(async () => ({ instanceId: await this.liveConnection() })); }

    // Record the instance and sending intent durably BEFORE any AddDownload.
    addDownload(requestId, payload, beforeSend) {
        return this.serial(async () => {
            const instanceId = await this.liveConnection();
            const message = { version: 1, action: 'AddDownload', requestId,
                extensionId: chrome.runtime.id, instanceId, payload };
            if (new TextEncoder().encode(JSON.stringify(message)).length > 65536) throw new Error('FrameTooLarge');
            await beforeSend(instanceId);
            return this.exchange(message);
        });
    }

    queryResult(record) {
        return this.serial(async () => {
            const instanceId = await this.liveConnection();
            if (instanceId !== record.instanceId) return { status: 'InstanceMismatch' };
            return this.exchange({ version: 1, action: 'GetRequestResult', requestId: record.requestId,
                extensionId: chrome.runtime.id, instanceId: record.instanceId });
        });
    }
}

export const nativeClient = new AriaUINativeClient();
