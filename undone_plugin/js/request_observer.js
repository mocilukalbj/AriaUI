// downloads has no webRequest requestId. Skip ambiguous associations.
// Observed headers stay in memory for at most 60 seconds; never persist them.
const KEY_HEADERS = ['cookie', 'referer', 'user-agent', 'origin', 'accept', 'accept-language'];

export function urlMatch(a, b) {
    if (a === b) return true;
    if (!a || !b) return false;
    try {
        const u1 = new URL(a);
        const u2 = new URL(b);
        u1.hash = '';
        u2.hash = '';
        return u1.href === u2.href;
    } catch {
        const clean = s => s.replace(/#.*$/, '');
        return clean(a) === clean(b);
    }
}

export function areEquivalent(a, b) {
    if (!a || !b) return false;
    if (!urlMatch(a.url, b.url)) return false;
    if (!urlMatch(a.originalUrl, b.originalUrl)) return false;
    if (a.method !== 'GET' || b.method !== 'GET') return false;
    if (Boolean(a.redirected) !== Boolean(b.redirected)) return false;
    if (a.unsafe || b.unsafe) return false;
    if (!a.responseTime || !b.responseTime) return false;
    if (!a.headers || !b.headers) return false;
    if ((a.initiator ?? null) !== (b.initiator ?? null)) return false;

    const extractKeyHeaders = req => {
        const map = new Map();
        for (const h of req.headers) {
            if (!h || typeof h.name !== 'string' || typeof h.value !== 'string') continue;
            const name = h.name.toLowerCase();
            if (KEY_HEADERS.includes(name)) {
                if (!map.has(name)) map.set(name, []);
                map.get(name).push(h.value);
            }
        }
        return map;
    };

    const mapA = extractKeyHeaders(a);
    const mapB = extractKeyHeaders(b);
    for (const name of KEY_HEADERS) {
        const listA = mapA.get(name) || [];
        const listB = mapB.get(name) || [];
        if (listA.length !== listB.length) return false;
        for (let i = 0; i < listA.length; i++) {
            if (listA[i] !== listB[i]) return false;
        }
    }
    return true;
}

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
            method: details.method, originalMethod: old?.originalMethod || details.method,
            unsafe: old?.unsafe || details.method !== 'GET',
            type: details.type, time: Date.now(), initiator: details.initiator, headers: null,
            used: old?.used || false,
            redirected: Boolean(old || old?.redirected) });
    }
    headers(details) {
        const request = this.requests.get(details.requestId);
        if (!request || !urlMatch(request.url, details.url)) return;
        request.headers = details.requestHeaders || [];
        if (request.headers.some(h => /^(authorization|proxy-authorization|range|if-range)$/i.test(h.name))) request.unsafe = true;
    }
    response(details) {
        const request = this.requests.get(details.requestId);
        if (!request || !urlMatch(request.url, details.url)) return;
        request.responseTime = Date.now();
        request.statusCode = details.statusCode;
        if (details.statusCode < 200 || details.statusCode >= 300 || details.statusCode === 206) request.unsafe = true;
    }
    claim(item) {
        const skip = reason => { this.lastReason = reason; return null; };
        this.prune();
        const finalUrl = item.finalUrl || item.url;
        const related = [...this.requests.values()].filter(r => {
            if (r.used) return false;
            if (!urlMatch(r.url, finalUrl)) return false;
            if (item.url && !urlMatch(r.originalUrl, item.url)) return false;
            return true;
        });
        if (!related.length) return skip('未观察到可用的网络请求：请检查扩展是否允许访问下载网站。');
        const start = Date.parse(item.startTime);
        if (!Number.isFinite(start)) return skip('无法核对下载开始时间，暂未自动接管。');
        const now = Date.now();

        const isRecent = (request, windowMs) => {
            const t = request.responseTime || request.time;
            return (now - t <= windowMs) && (Math.abs(t - start) <= windowMs);
        };

        const candidates10 = related.filter(r => isRecent(r, 10000));
        let candidates = candidates10;
        if (!candidates.length) {
            const candidates15 = related.filter(r => isRecent(r, 15000));
            if (!candidates15.length) return skip('网络请求与下载事件时间不匹配，暂未自动接管。');
            candidates = candidates15;
        }

        let chosen = null;
        if (candidates.length === 1) {
            chosen = candidates[0];
        } else {
            // Concurrency ambiguity checks apply ONLY when multiple candidates exist
            if (candidates.some(r => !r.responseTime)) {
                return skip('同一地址存在多个近期未处理请求，无法确定应交接哪一个：存在尚未取得响应的请求。');
            }
            if (candidates.some(r => r.method !== 'GET')) {
                return skip('同一地址存在多个近期未处理请求，无法确定应交接哪一个：存在无法排除的 POST 或非 GET 请求。');
            }
            if (candidates.some(r => r.unsafe)) {
                return skip('同一地址存在多个近期未处理请求，无法确定应交接哪一个：存在不满足接管条件的请求。');
            }
            const allEquiv = candidates.every(r => areEquivalent(candidates[0], r));
            if (!allEquiv) {
                return skip('同一地址存在多个近期未处理请求，无法确定应交接哪一个：关键请求头或参数不一致。');
            }
            chosen = candidates[0];
        }

        if (!urlMatch(chosen.url, finalUrl)) return skip('请求最终地址与下载地址不匹配。');
        if (chosen.used) return skip('网络请求已被之前的下载使用。');
        const isRedirect = Boolean(chosen.redirected || (chosen.originalUrl && !urlMatch(chosen.originalUrl, chosen.url)) || (chosen.originalMethod && chosen.method && chosen.originalMethod !== chosen.method));
        if (isRedirect && chosen.originalMethod && chosen.originalMethod !== 'GET') {
            return skip(`仅支持自动接管源自 GET 的请求（当前为 ${chosen.originalMethod} 重定向），暂未自动接管。`);
        }
        if (chosen.method !== 'GET') return skip('仅支持自动接管 GET 请求，当前为 ' + (chosen.method || '未知方法') + '。');
        if (!chosen.responseTime) return skip('网络请求尚未取得响应，暂未自动接管。');
        if (!chosen.headers) return skip('未捕获到请求头信息，无法交接。');
        if (chosen.headers.some(h => /^(authorization|proxy-authorization)$/i.test(h.name))) {
            return skip('请求包含认证信息，暂未自动接管。');
        }
        if (chosen.headers.some(h => /^(range|if-range)$/i.test(h.name))) {
            return skip('请求包含范围请求头，暂未自动接管。');
        }
        if (chosen.statusCode && (chosen.statusCode < 200 || chosen.statusCode >= 300 || chosen.statusCode === 206)) {
            return skip('网络响应状态异常（' + chosen.statusCode + '），暂未自动接管。');
        }
        if (chosen.unsafe) return skip('请求不满足安全接管条件。');
        if (item.referrer && chosen.initiator) {
            try {
                if (new URL(item.referrer).origin !== new URL(chosen.initiator).origin) {
                    return skip('下载来源与请求来源不匹配。');
                }
            } catch {
                return skip('无法核对下载来源。');
            }
        }
        chosen.used = true;
        return chosen;
    }
}
