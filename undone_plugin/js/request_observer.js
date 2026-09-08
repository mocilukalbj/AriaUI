// downloads has no webRequest requestId. Skip ambiguous associations.
// Observed headers stay in memory for at most 60 seconds; never persist them.
export class RequestObserver {
    constructor() { this.requests = new Map(); }
    prune() {
        const cutoff = Date.now() - 60000;
        for (const [id, request] of this.requests) if (request.time < cutoff) this.requests.delete(id);
        while (this.requests.size > 512) this.requests.delete(this.requests.keys().next().value);
    }
    before(details) {
        this.prune();
        if (details.incognito) return;
        const old = this.requests.get(details.requestId);
        this.requests.set(details.requestId, { url: details.url, originalUrl: old?.originalUrl || details.url,
            method: details.method, unsafe: old?.unsafe || details.method !== 'GET',
            type: details.type, time: Date.now(), initiator: details.initiator, headers: null,
            used: old?.used || false });
    }
    headers(details) {
        const request = this.requests.get(details.requestId);
        if (!request || request.url !== details.url) return;
        request.headers = details.requestHeaders || [];
        if (request.headers.some(h => /^(authorization|proxy-authorization|range|if-range)$/i.test(h.name))) request.unsafe = true;
    }
    response(details) {
        const request = this.requests.get(details.requestId);
        if (!request || request.url !== details.url) return;
        request.responseTime = Date.now();
        if (details.statusCode < 200 || details.statusCode >= 300 || details.statusCode === 206) request.unsafe = true;
    }
    claim(item) {
        const skip = reason => { this.lastReason = reason; return null; };
        this.prune();
        const finalUrl = item.finalUrl || item.url;
        const matches = [...this.requests.values()].filter(r => r.url === finalUrl || r.originalUrl === item.url);
        if (!matches.length) return skip('未观察到对应网络请求：请检查扩展是否允许访问下载网站。');
        if (matches.length !== 1) return skip('同一地址存在多个请求，无法确定应交接哪一个。');
        const request = matches[0];
        if (request.url !== finalUrl || request.used || request.unsafe || request.method !== 'GET' ||
            !request.headers || !['main_frame', 'sub_frame', 'other'].includes(request.type)) return skip('请求不满足接管条件：可能是非 GET、认证/范围请求、请求已使用或请求头缺失。');
        const start = Date.parse(item.startTime);
        if (!request.responseTime || Date.now() - request.responseTime > 5000 ||
            !Number.isFinite(start) || Math.abs(request.responseTime - start) > 5000) return skip('网络请求与下载事件时间不匹配，暂未自动接管。');
        if (item.referrer && request.initiator) {
            try { if (new URL(item.referrer).origin !== new URL(request.initiator).origin) return skip('下载来源与请求来源不匹配。'); }
            catch { return skip('无法核对下载来源。'); }
        }
        request.used = true;
        return request;
    }
}
