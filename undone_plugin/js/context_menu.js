import { makePayload } from './settings.js';

export function resolveReferer(sourceUrlStr, targetUrlStr) {
    if (!sourceUrlStr || !targetUrlStr) return null;
    try {
        const sourceUrl = new URL(sourceUrlStr);
        const targetUrl = new URL(targetUrlStr);
        if (!['http:', 'https:'].includes(sourceUrl.protocol) || !['http:', 'https:'].includes(targetUrl.protocol)) {
            return null;
        }
        // HTTPS -> HTTP downgrade: do not attach Referer
        if (sourceUrl.protocol === 'https:' && targetUrl.protocol === 'http:') {
            return null;
        }
        // Strip credentials and fragment
        sourceUrl.username = '';
        sourceUrl.password = '';
        sourceUrl.hash = '';

        if (sourceUrl.origin === targetUrl.origin) {
            return sourceUrl.href;
        }
        // Conservative cross-origin: origin only
        return sourceUrl.origin + '/';
    } catch {
        return null;
    }
}

export function buildCookieHeader(cookies) {
    if (!Array.isArray(cookies) || !cookies.length) return null;
    // RFC 6265 §5.4: Cookies with longer paths are listed before cookies with shorter paths
    const sorted = [...cookies].sort((a, b) => {
        const pathLenA = (a?.path || '/').length;
        const pathLenB = (b?.path || '/').length;
        if (pathLenB !== pathLenA) return pathLenB - pathLenA;
        return (a?.creationTime || 0) - (b?.creationTime || 0);
    });

    const parts = [];
    for (const c of sorted) {
        if (!c || typeof c.name !== 'string' || typeof c.value !== 'string') continue;
        if (/[\r\n\0]/.test(c.name) || /[\r\n\0]/.test(c.value)) continue;
        parts.push(`${c.name}=${c.value}`);
    }
    if (!parts.length) return null;
    const headerValue = parts.join('; ');
    // AppGatewayService and HTTP protocol limit single header lines to 8192 bytes ("Cookie: " + value)
    if (new TextEncoder().encode('Cookie: ' + headerValue).length > 8192) {
        throw new Error('目标网站 Cookie 超出请求头单项大小限制（8KB）。');
    }
    return headerValue;
}

export function getCookieIdentity(c, fallbackPk = null) {
    if (!c) return '';
    const pk = c.partitionKey ?? fallbackPk;
    let pkStr = '';
    if (pk) {
        if (typeof pk === 'string') {
            pkStr = pk;
        } else {
            pkStr = `${pk.topLevelSite || ''}:${Boolean(pk.hasCrossSiteAncestor)}`;
        }
    }
    return `${c.name}@${c.domain || ''}@${c.path || '/'}#${pkStr}`;
}

export async function verifyRedirectScope(targetUrlStr, cookieHeader, options = {}) {
    if (!cookieHeader) return targetUrlStr;
    const fetchFn = options.fetch !== undefined ? options.fetch : (typeof fetch === 'function' ? fetch : null);
    if (!fetchFn) {
        throw new Error('缺少网络探测环境，无法验证凭据安全。');
    }

    const targetUrl = new URL(targetUrlStr);
    const timeoutMs = options.timeoutMs || 4000;

    let resp = null;
    let headFailed = false;

    const createTimeoutTracker = ms => {
        const controller = new AbortController();
        let timerId = null;
        const promise = new Promise((_, reject) => {
            timerId = setTimeout(() => {
                try { controller.abort(); } catch {}
                reject(new Error('跳转探测超时，无法验证凭据安全。'));
            }, ms);
        });
        return {
            controller,
            promise,
            cleanup: () => { if (timerId) clearTimeout(timerId); }
        };
    };

    const checkRedirectOrigin = response => {
        if (!response || !response.redirected) return;
        if (!response.url) {
            throw new Error('重定向目标地址无法解析。');
        }
        let finalUrl;
        try {
            finalUrl = new URL(response.url);
        } catch {
            throw new Error('重定向目标地址无法解析。');
        }
        if (finalUrl.origin !== targetUrl.origin) {
            throw new Error(`目标链接存在跨域重定向（从 ${targetUrl.origin} 跳转至 ${finalUrl.origin}）。为防止 Cookie 凭据泄露，已限制此类跨域跳转；如为公开链接请使用“仅发送链接到 AriaUI”。`);
        }
    };

    const headTracker = createTimeoutTracker(timeoutMs);
    try {
        resp = await Promise.race([
            fetchFn(targetUrl.href, {
                method: 'HEAD',
                // The browser chooses probe cookies; these may differ from the
                // right-click frame's cookies forwarded to AriaUI.
                credentials: 'include',
                signal: headTracker.controller.signal
            }),
            headTracker.promise
        ]);
    } catch (headErr) {
        if (headTracker.controller.signal.aborted || headErr?.name === 'AbortError' || headErr?.message?.includes('超时')) {
            throw new Error('跳转探测超时，无法验证凭据安全。');
        }
        headFailed = true;
    } finally {
        headTracker.cleanup();
    }

    // Check redirect on HEAD response first (if any)
    if (resp) {
        checkRedirectOrigin(resp);
    }

    // If HEAD failed with network error, or returned no response, or returned non-ok status (405, 501, 400, 403, 500, etc.),
    // fall back to probing with GET (Range: bytes=0-0)
    if (headFailed || !resp || !resp.ok || resp.status === 405 || resp.status === 501) {
        const getTracker = createTimeoutTracker(timeoutMs);
        try {
            resp = await Promise.race([
                fetchFn(targetUrl.href, {
                    method: 'GET',
                    credentials: 'include',
                    headers: { 'Range': 'bytes=0-0' },
                    signal: getTracker.controller.signal
                }),
                getTracker.promise
            ]);
        } catch (getErr) {
            if (getTracker.controller.signal.aborted || getErr?.name === 'AbortError' || getErr?.message?.includes('超时')) {
                throw new Error('跳转探测超时，无法验证凭据安全。');
            }
            throw new Error(`跳转探测失败：${getErr?.message || '网络请求错误'}，无法验证凭据安全。`);
        } finally {
            getTracker.cleanup();
            try { getTracker.controller.abort(); } catch {}
        }
    }

    if (!resp) {
        throw new Error('跳转探测失败：未收到有效响应，无法验证凭据安全。');
    }

    // Check redirect on GET response
    checkRedirectOrigin(resp);
    if (!resp.ok) {
        throw new Error(`跳转探测失败：HTTP ${resp.status}，未取得成功响应，无法验证凭据安全。`);
    }

    return targetUrlStr;
}

export async function buildLinkContextPayload(info, tab, options = {}) {
    const targetUrlStr = info?.linkUrl;
    if (!targetUrlStr) throw new Error('未获取到目标链接地址。');
    let targetUrl;
    try {
        targetUrl = new URL(targetUrlStr);
    } catch {
        throw new Error('目标链接无法解析为有效 URL。');
    }

    if (targetUrl.protocol === 'magnet:') {
        throw new Error('携带 Cookie / Referer 仅支持 HTTP / HTTPS 链接；magnet 链接请使用“仅发送链接到 AriaUI”。');
    }
    if (!['http:', 'https:'].includes(targetUrl.protocol)) {
        throw new Error('仅支持 HTTP / HTTPS 链接下载。');
    }
    if (targetUrl.username || targetUrl.password) {
        throw new Error('链接包含嵌入认证信息（用户名或密码），暂不支持自动提取凭据。');
    }

    if (typeof chrome === 'undefined' || !chrome?.cookies?.getAll) {
        throw new Error('缺少 Cookie 读取权限，无法获取目标链接凭据。');
    }

    const queryDetails = { url: targetUrl.href };
    if (tab?.cookieStoreId) {
        queryDetails.storeId = tab.cookieStoreId;
    }

    let unpartitioned = [];
    try {
        unpartitioned = await chrome.cookies.getAll(queryDetails);
    } catch (err) {
        throw new Error(`读取目标网站 Cookie 失败：${err?.message || '未知错误'}`);
    }

    let partitionKey = null;
    let getPartitionKeySupported = false;
    if (typeof chrome !== 'undefined' && typeof chrome.cookies?.getPartitionKey === 'function') {
        getPartitionKeySupported = true;
        try {
            const frameDetails = {};
            if (typeof info?.frameId === 'number') frameDetails.frameId = info.frameId;
            if (typeof tab?.id === 'number') frameDetails.tabId = tab.id;
            if (info?.documentId) frameDetails.documentId = info.documentId;
            if (Object.keys(frameDetails).length > 0) {
                const result = await chrome.cookies.getPartitionKey(frameDetails);
                partitionKey = result?.partitionKey ?? (result?.topLevelSite ? result : null);
            }
        } catch {
            partitionKey = null;
        }
    }

    if (!partitionKey && !getPartitionKeySupported) {
        let topLevelSite = null;
        if (tab?.url) {
            try {
                const pageUrl = new URL(tab.url);
                if (['http:', 'https:'].includes(pageUrl.protocol)) {
                    topLevelSite = pageUrl.origin;
                }
            } catch {}
        }
        if (topLevelSite) {
            partitionKey = { topLevelSite };
        }
    }

    let partitioned = [];
    if (partitionKey) {
        try {
            partitioned = await chrome.cookies.getAll({
                ...queryDetails,
                partitionKey
            });
        } catch {
            // Browser might not support partitionKey parameter; ignore safely.
        }
    }

    // Merge partitioned cookies without altering values; scoped by name, domain, path, and partition identity
    const cookieMap = new Map();
    for (const c of unpartitioned) {
        if (c?.name) cookieMap.set(getCookieIdentity(c, null), c);
    }
    for (const c of partitioned) {
        if (c?.name) cookieMap.set(getCookieIdentity(c, partitionKey), c);
    }

    const cookieHeader = buildCookieHeader([...cookieMap.values()]);

    if (cookieHeader) {
        await verifyRedirectScope(targetUrl.href, cookieHeader, options);
    }

    const sourceUrlStr = info?.frameUrl || info?.pageUrl || tab?.url;
    const referer = resolveReferer(sourceUrlStr, targetUrl.href);
    const userAgent = typeof navigator !== 'undefined' ? navigator.userAgent : null;

    const headers = [];
    if (cookieHeader) headers.push({ name: 'Cookie', value: cookieHeader });
    if (referer) headers.push({ name: 'Referer', value: referer });
    if (userAgent) headers.push({ name: 'User-Agent', value: userAgent });

    const payload = makePayload(targetUrl.href, '', headers);
    // Native Messaging frame size limit is 64KB (65536 bytes)
    if (new TextEncoder().encode(JSON.stringify(payload)).length > 60000) {
        throw new Error('下载凭据与上下文信息超出宿主单帧限制（64KB）。');
    }
    return payload;
}
