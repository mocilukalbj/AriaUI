/**
 * AriaUI Native Messaging Client (§8.1–§8.4, G01–G12)
 * Manages communication with AriaUI Thin Host via chrome.runtime.connectNative.
 * Enforces:
 * - Only cancels browser download AFTER receiving real 16-hex GID.
 * - Filters unsupported schemes (POST, blob, data, etc.) and hands back to browser.
 * - On timeout or disconnect, queries GetRequestResult before failing; does not auto-re-add.
 */

export class AriaUINativeClient {
    constructor(hostName = "com.ariaui.downloader") {
        this.hostName = hostName;
        this.port = null;
        this.instanceId = null;
        this.pendingRequests = new Map();
        this.requestTimeouts = new Map();
    }

    ensureConnected() {
        if (this.port) return Promise.resolve(this.instanceId);

        return new Promise((resolve, reject) => {
            try {
                this.port = chrome.runtime.connectNative(this.hostName);
            } catch (err) {
                return reject(err);
            }

            const handshakeTimeout = setTimeout(() => {
                this.cleanup();
                reject(new Error("Handshake timeout with AriaUI host."));
            }, 10000);

            this.port.onMessage.addListener((msg) => {
                if (msg.action === "Handshake" || (msg.status === "Success" && msg.instanceId && !msg.requestId)) {
                    clearTimeout(handshakeTimeout);
                    this.instanceId = msg.instanceId;
                    resolve(this.instanceId);
                    return;
                }

                if (msg.requestId && this.pendingRequests.has(msg.requestId)) {
                    const { resolve: reqResolve, reject: reqReject } = this.pendingRequests.get(msg.requestId);
                    this.pendingRequests.delete(msg.requestId);
                    if (this.requestTimeouts.has(msg.requestId)) {
                        clearTimeout(this.requestTimeouts.get(msg.requestId));
                        this.requestTimeouts.delete(msg.requestId);
                    }

                    if (msg.status === "Success") {
                        reqResolve(msg);
                    } else {
                        reqReject(msg);
                    }
                }
            });

            this.port.onDisconnect.addListener(() => {
                const err = chrome.runtime.lastError ? chrome.runtime.lastError.message : "Port disconnected.";
                clearTimeout(handshakeTimeout);
                this.cleanup();
                for (const [reqId, handler] of this.pendingRequests.entries()) {
                    handler.reject({ status: "Disconnected", error: err, requestId: reqId });
                }
                this.pendingRequests.clear();
            });

            // Send Handshake
            this.port.postMessage({
                version: 1,
                action: "Handshake"
            });
        });
    }

    cleanup() {
        if (this.port) {
            try { this.port.disconnect(); } catch {}
            this.port = null;
        }
        this.instanceId = null;
    }

    isSupportedUrl(url) {
        if (!url) return false;
        const lower = url.trim().toLowerCase();
        return lower.startsWith("http://") || lower.startsWith("https://") || lower.startsWith("magnet:?");
    }

    async addDownload(downloadItem, headersList = []) {
        if (!this.isSupportedUrl(downloadItem.url)) {
            return {
                captured: false,
                reason: "UnsupportedScheme",
                message: "Only HTTP, HTTPS and magnet downloads are supported."
            };
        }

        await this.ensureConnected();

        const requestId = "req-" + Date.now() + "-" + Math.random().toString(36).substring(2, 8);
        const allowedHeaders = [];

        for (const h of headersList) {
            if (typeof h === "string" && !h.includes("\r") && !h.includes("\n") && !h.includes("\0")) {
                if (allowedHeaders.length < 32) {
                    allowedHeaders.push(h);
                }
            }
        }

        const message = {
            version: 1,
            action: "AddDownload",
            requestId: requestId,
            extensionId: chrome.runtime.id || "aria2-explorer",
            instanceId: this.instanceId,
            payload: {
                url: downloadItem.url,
                out: downloadItem.filename ? downloadItem.filename.replace(/^.*[\\\/]/, '') : undefined,
                referer: (downloadItem.referrer && downloadItem.referrer !== "about:blank") ? downloadItem.referrer : undefined,
                userAgent: navigator.userAgent,
                headers: allowedHeaders.length > 0 ? allowedHeaders : undefined
            }
        };

        return new Promise((resolve, reject) => {
            const timeoutId = setTimeout(async () => {
                this.pendingRequests.delete(requestId);
                this.requestTimeouts.delete(requestId);

                // Unknown outcome: query original request result before giving up
                try {
                    const outcome = await this.queryResult(requestId);
                    if (outcome && outcome.status === "Success" && outcome.gid) {
                        return resolve(outcome);
                    }
                } catch {}

                reject({ status: "Timeout", requestId: requestId, error: "Request timed out; status unknown." });
            }, 10000);

            this.requestTimeouts.set(requestId, timeoutId);
            this.pendingRequests.set(requestId, { resolve, reject });

            try {
                this.port.postMessage(message);
            } catch (err) {
                clearTimeout(timeoutId);
                this.pendingRequests.delete(requestId);
                this.requestTimeouts.delete(requestId);
                reject(err);
            }
        });
    }

    async queryResult(requestId) {
        if (!this.port || !this.instanceId) {
            await this.ensureConnected();
        }

        const message = {
            version: 1,
            action: "GetRequestResult",
            requestId: requestId,
            extensionId: chrome.runtime.id || "aria2-explorer",
            instanceId: this.instanceId
        };

        return new Promise((resolve, reject) => {
            const timeoutId = setTimeout(() => {
                this.pendingRequests.delete(requestId);
                reject(new Error("Query timed out."));
            }, 5000);

            this.pendingRequests.set(requestId, {
                resolve: (res) => {
                    clearTimeout(timeoutId);
                    this.pendingRequests.delete(requestId);
                    resolve(res);
                },
                reject: (err) => {
                    clearTimeout(timeoutId);
                    this.pendingRequests.delete(requestId);
                    reject(err);
                }
            });

            try {
                this.port.postMessage(message);
            } catch (err) {
                clearTimeout(timeoutId);
                this.pendingRequests.delete(requestId);
                reject(err);
            }
        });
    }
}

export const nativeClient = new AriaUINativeClient();
