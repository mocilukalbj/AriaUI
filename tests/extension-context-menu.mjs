import assert from 'node:assert/strict';
import fs from 'node:fs';

import { resolveReferer, buildCookieHeader, buildLinkContextPayload, verifyRedirectScope } from '../undone_plugin/js/context_menu.js';

// 1. resolveReferer tests
// Same-origin -> full URL without credentials and fragment
assert.equal(
    resolveReferer('https://user:pass@example.com/path/page.html?q=1#section', 'https://example.com/downloads/file.zip'),
    'https://example.com/path/page.html?q=1'
);

// Cross-origin -> origin only
assert.equal(
    resolveReferer('https://source.org/page.html?foo=bar', 'https://download.com/file.zip'),
    'https://source.org/'
);

// HTTPS -> HTTP downgrade -> null
assert.equal(
    resolveReferer('https://secure.com/page', 'http://insecure.com/file.zip'),
    null
);

// HTTP -> HTTP cross-origin -> origin only
assert.equal(
    resolveReferer('http://source.com/page', 'http://target.com/file.zip'),
    'http://source.com/'
);

// Non-HTTP/HTTPS -> null
assert.equal(resolveReferer('chrome://extensions/', 'https://example.com/file.zip'), null);
assert.equal(resolveReferer(null, 'https://example.com/file.zip'), null);

// 2. buildCookieHeader tests
assert.equal(buildCookieHeader([]), null);
assert.equal(buildCookieHeader(null), null);
assert.equal(
    buildCookieHeader([{ name: 'sid', value: '123' }, { name: 'theme', value: 'dark' }]),
    'sid=123; theme=dark'
);

// RFC 6265 path sorting: longer paths before shorter paths
assert.equal(
    buildCookieHeader([
        { name: 'root', value: '1', path: '/' },
        { name: 'sub', value: '2', path: '/downloads' },
        { name: 'deep', value: '3', path: '/downloads/deep' }
    ]),
    'deep=3; sub=2; root=1'
);

// Control characters in name or value are dropped
assert.equal(
    buildCookieHeader([
        { name: 'bad\r\n', value: 'hack' },
        { name: 'clean', value: 'good' }
    ]),
    'clean=good'
);

// Oversized cookies (> 8KB including "Cookie: " prefix) must throw
const hugeValue = 'x'.repeat(8185); // 8185 + 4 ("big=") + 8 ("Cookie: ") = 8197 > 8192
assert.throws(
    () => buildCookieHeader([{ name: 'big', value: hugeValue }]),
    /超出请求头单项大小限制/
);

// 3. buildLinkContextPayload tests
// Mock chrome API
const storedCookies = [
    { name: 'session', value: 'secret123', path: '/', domain: 'example.com' }
];

globalThis.chrome = {
    cookies: {
        getAll: async details => {
            if (details.partitionKey) {
                return [{ name: 'partitioned_cookie', value: 'part_val', path: '/' }];
            }
            return structuredClone(storedCookies);
        }
    }
};

Object.defineProperty(globalThis.navigator, 'userAgent', { value: 'Mozilla/5.0 TestAgent', configurable: true });
globalThis.fetch = async url => ({ url: String(url), redirected: false, status: 200, ok: true });

// Valid contextual download
const info = {
    linkUrl: 'https://example.com/files/document.pdf',
    pageUrl: 'https://example.com/home'
};
const tab = { url: 'https://example.com/home', cookieStoreId: '0' };

const payload = await buildLinkContextPayload(info, tab);
assert.equal(payload.url, 'https://example.com/files/document.pdf');
assert.equal(payload.referer, 'https://example.com/home');
assert.equal(payload.userAgent, 'Mozilla/5.0 TestAgent');
assert.ok(payload.headers.some(h => h.startsWith('Cookie: ') && h.includes('session=secret123') && h.includes('partitioned_cookie=part_val')));

// Magnet link should throw clear instruction
await assert.rejects(
    () => buildLinkContextPayload({ linkUrl: 'magnet:?xt=urn:btih:1234567890abcdef1234567890abcdef12345678' }, tab),
    /magnet 链接请使用“仅发送链接到 AriaUI”/
);

// Unsupported protocols
await assert.rejects(
    () => buildLinkContextPayload({ linkUrl: 'ftp://example.com/file.zip' }, tab),
    /仅支持 HTTP \/ HTTPS 链接下载/
);

// Missing cookies permission
const originalCookies = globalThis.chrome.cookies;
delete globalThis.chrome.cookies;
await assert.rejects(
    () => buildLinkContextPayload(info, tab),
    /缺少 Cookie 读取权限/
);
globalThis.chrome.cookies = originalCookies;

// Multi-domain cookies with same name are both retained
globalThis.chrome.cookies = {
    getAll: async () => [
        { name: 'auth', value: 'domain_val', domain: '.example.com', path: '/' },
        { name: 'auth', value: 'host_val', domain: 'sub.example.com', path: '/' }
    ]
};
const multiPayload = await buildLinkContextPayload({ linkUrl: 'https://sub.example.com/file.zip' }, tab);
const cookieHeaderLine = multiPayload.headers.find(h => h.startsWith('Cookie: '));
assert.ok(cookieHeaderLine.includes('auth=domain_val'));
assert.ok(cookieHeaderLine.includes('auth=host_val'));

// Frame size limit rejection (> 60KB)
globalThis.chrome.cookies = {
    getAll: async () => {
        const bigList = [];
        for (let i = 0; i < 20; i++) {
            bigList.push({ name: `cookie_${i}`, value: 'y'.repeat(3500), path: '/' });
        }
        return bigList;
    }
};
// Note: individual cookie headers may be smaller than 8KB, but total payload exceeds 60KB
await assert.rejects(
    () => buildLinkContextPayload({ linkUrl: 'https://example.com/file.zip' }, tab),
    /超出/
);
// 4. Partitioned and unpartitioned cookies with same name, domain, and path must NOT overwrite each other
globalThis.chrome.cookies = {
    getAll: async details => {
        if (details.partitionKey) {
            return [{ name: 'account', value: 'partitioned_val', domain: 'example.com', path: '/', partitionKey: details.partitionKey }];
        }
        return [{ name: 'account', value: 'normal_val', domain: 'example.com', path: '/' }];
    },
    getPartitionKey: async details => {
        assert.equal(details.frameId, 1);
        assert.equal(details.tabId, 42);
        assert.equal(details.documentId, 'doc-123');
        return { partitionKey: { topLevelSite: 'https://parent.com', hasCrossSiteAncestor: true } };
    }
};

const collisionInfo = {
    linkUrl: 'https://example.com/files/download.zip',
    frameId: 1,
    documentId: 'doc-123',
    pageUrl: 'https://parent.com'
};
const collisionTab = { id: 42, url: 'https://parent.com' };

const collisionPayload = await buildLinkContextPayload(collisionInfo, collisionTab);
const collisionCookieLine = collisionPayload.headers.find(h => h.startsWith('Cookie: '));
assert.ok(collisionCookieLine.includes('account=normal_val'), 'Normal cookie must be preserved');
assert.ok(collisionCookieLine.includes('account=partitioned_val'), 'Partitioned cookie must be preserved');

// 5. Cross-origin redirect restrictions when Cookie is present
globalThis.chrome.cookies = originalCookies;

// Cross-origin redirect with Cookie must be rejected to prevent credential leak
const crossOriginFetch = async () => ({
    url: 'https://untrusted-cdn.com/archive.zip',
    redirected: true,
    status: 200, ok: true
});
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: crossOriginFetch }),
    /目标链接存在跨域重定向.*为防止 Cookie 凭据泄露，已限制此类跨域跳转/
);

// Same-origin redirect with Cookie is safe and must succeed
const sameOriginFetch = async () => ({
    url: 'https://example.com/files/final_destination.zip',
    redirected: true,
    status: 200, ok: true
});
const sameOriginPayload = await buildLinkContextPayload(info, tab, { fetch: sameOriginFetch });
assert.ok(sameOriginPayload.headers.some(h => h.startsWith('Cookie: ')));

// Cross-origin redirect without Cookie is permitted (no credentials to leak)
globalThis.chrome.cookies = {
    getAll: async () => []
};
const noCookiePayload = await buildLinkContextPayload(info, tab, { fetch: crossOriginFetch });
assert.ok(noCookiePayload, 'Download without cookies can proceed without redirect rejection');
assert.equal(Boolean(noCookiePayload.headers?.some(h => h.startsWith('Cookie: '))), false);

globalThis.chrome.cookies = originalCookies;

// 6. Probe failure & HEAD 405 fallback tests
// Probe timeout when Cookie is present must reject to prevent unsafe release
await assert.rejects(
    () => buildLinkContextPayload(info, tab, {
        timeoutMs: 20,
        fetch: () => new Promise(resolve => setTimeout(resolve, 80))
    }),
    /跳转探测超时/
);

// Probe network error when Cookie is present must reject to prevent unsafe release
await assert.rejects(
    () => buildLinkContextPayload(info, tab, {
        fetch: () => { throw new TypeError('Network connection refused'); }
    }),
    /跳转探测失败/
);

// HEAD returns 405 -> triggers GET fallback -> GET redirects to cross-origin -> rejects
let head405Called = false;
let getFallbackCalled = false;
const head405CrossOriginFetch = async (url, opts) => {
    if (opts.method === 'HEAD') {
        head405Called = true;
        return { status: 405, redirected: false, url };
    }
    if (opts.method === 'GET') {
        getFallbackCalled = true;
        return { status: 200, ok: true, redirected: true, url: 'https://untrusted-cdn.com/archive.zip' };
    }
};
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: head405CrossOriginFetch }),
    /目标链接存在跨域重定向.*为防止 Cookie 凭据泄露，已限制此类跨域跳转/
);
assert.ok(head405Called, 'HEAD must be called first');
assert.ok(getFallbackCalled, 'GET fallback must be triggered when HEAD returns 405');

// HEAD returns 405 -> triggers GET fallback -> GET succeeds with same-origin -> passes
const head405SameOriginFetch = async (url, opts) => {
    if (opts.method === 'HEAD') {
        return { status: 405, redirected: false, url };
    }
    if (opts.method === 'GET') {
        return { status: 200, ok: true, redirected: true, url: 'https://example.com/files/final_destination.zip' };
    }
};
const head405SameOriginPayload = await buildLinkContextPayload(info, tab, { fetch: head405SameOriginFetch });
assert.ok(head405SameOriginPayload.headers.some(h => h.startsWith('Cookie: ')));

// HEAD returns 405 -> triggers GET fallback -> GET encounters network error -> rejects
const head405GetFailsFetch = async (url, opts) => {
    if (opts.method === 'HEAD') {
        return { status: 405, redirected: false, url };
    }
    if (opts.method === 'GET') {
        throw new TypeError('GET connection timeout');
    }
};
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: head405GetFailsFetch }),
    /跳转探测失败.*GET connection timeout/
);

// HTTP -> HTTPS cross-origin redirect is intercepted (not passed as same-origin)
const httpToHttpsFetch = async () => ({
    url: 'https://example.com/files/document.pdf',
    redirected: true,
    status: 200, ok: true
});
const httpTargetInfo = {
    linkUrl: 'http://example.com/files/document.pdf',
    pageUrl: 'http://example.com/home'
};
await assert.rejects(
    () => buildLinkContextPayload(httpTargetInfo, tab, { fetch: httpToHttpsFetch }),
    /目标链接存在跨域重定向.*从 http:\/\/example.com 跳转至 https:\/\/example.com/
);

// 7. Non-ok HEAD responses (400, 403, 500) must trigger GET fallback
let head400Called = false;
let getFallbackFrom400Called = false;
const head400CrossOriginFetch = async (url, opts) => {
    if (opts.method === 'HEAD') {
        head400Called = true;
        return { status: 400, ok: false, redirected: false, url };
    }
    if (opts.method === 'GET') {
        getFallbackFrom400Called = true;
        return { status: 200, ok: true, redirected: true, url: 'https://untrusted-cdn.com/archive.zip' };
    }
};
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: head400CrossOriginFetch }),
    /目标链接存在跨域重定向.*为防止 Cookie 凭据泄露，已限制此类跨域跳转/
);
assert.ok(head400Called, 'HEAD must be called first for 400 response');
assert.ok(getFallbackFrom400Called, 'GET fallback must be triggered when HEAD returns 400');

// HEAD returns 403 -> triggers GET fallback -> GET succeeds with same-origin
const head403SameOriginFetch = async (url, opts) => {
    if (opts.method === 'HEAD') {
        return { status: 403, ok: false, redirected: false, url };
    }
    if (opts.method === 'GET') {
        return { status: 200, ok: true, redirected: true, url: 'https://example.com/files/final.zip' };
    }
};
const head403Payload = await buildLinkContextPayload(info, tab, { fetch: head403SameOriginFetch });
assert.ok(head403Payload.headers.some(h => h.startsWith('Cookie: ')));

// HEAD redirecting to cross-origin endpoint returning 405 must reject immediately
const headRedirect405Fetch = async () => ({
    status: 405,
    ok: false,
    redirected: true,
    url: 'https://evil.com/blocked'
});
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: headRedirect405Fetch }),
    /目标链接存在跨域重定向/
);

// Missing fetch function when Cookie is present must fail closed
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: null }),
    /缺少网络探测环境/
);

// Fetch returning null response must reject
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: async () => null }),
    /未收到有效响应/
);

// Redirected response missing or with invalid url must reject
await assert.rejects(
    () => buildLinkContextPayload(info, tab, { fetch: async () => ({ redirected: true, url: '', status: 200, ok: true }) }),
    /重定向目标地址无法解析/
);

// chrome.cookies.getPartitionKey returning { partitionKey: null } must NOT fabricate topLevelSite
let partitionKeyQueriedInNullTest = undefined;
globalThis.chrome.cookies = {
    getAll: async details => {
        if ('partitionKey' in details) {
            partitionKeyQueriedInNullTest = details.partitionKey;
        }
        return [{ name: 'safe_cookie', value: 'v1', domain: 'example.com', path: '/' }];
    },
    getPartitionKey: async () => ({ partitionKey: null })
};
await buildLinkContextPayload({ linkUrl: 'https://example.com/file.zip', frameId: 0 }, { id: 1, url: 'https://other.com' });
assert.equal(partitionKeyQueriedInNullTest, undefined, 'Unpartitioned context must not fabricate partitionKey from tab origin');

globalThis.chrome.cookies = originalCookies;

// Failed GET probes must not pass simply because their final origin matches.
for (const status of [401, 403, 405, 416, 500, 503]) {
    const methods = [];
    await assert.rejects(() => verifyRedirectScope(info.linkUrl, 'session=demo', {
        fetch: async (url, options) => {
            methods.push(options.method);
            return new Response(null, { status });
        }
    }), new RegExp(`跳转探测失败：HTTP ${status}`));
    assert.deepEqual(methods, ['HEAD', 'GET']);
}

// Probe credentials come from the browser, never a scripted Cookie header.
for (const fallback of [false, true]) {
    const methods = [];
    await verifyRedirectScope(info.linkUrl, 'session=demo', {
        fetch: async (url, options) => {
            methods.push(options.method);
            assert.equal(options.credentials, 'include');
            const headers = new Headers(options.headers);
            assert.equal(headers.has('Cookie'), false);
            if (options.method === 'GET') assert.equal(headers.get('Range'), 'bytes=0-0');
            return new Response(null, { status: fallback && options.method === 'HEAD' ? 405 : 206 });
        }
    });
    assert.deepEqual(methods, fallback ? ['HEAD', 'GET'] : ['HEAD']);
}

console.log('PASS context menu referer resolution, cookie header builder, context payload construction, partitioned cookies, permission checks, scheme validations, cross-origin redirect restrictions, failed GET rejection, browser-managed probe cookies');
