import assert from 'node:assert/strict';
import fs from 'node:fs';
const source = fs.readFileSync(new URL('../undone_plugin/js/request_observer.js', import.meta.url));
const { RequestObserver } = await import('data:text/javascript;base64,' + source.toString('base64'));
const realNow = Date.now;
let now = 1800000000000;
Date.now = () => now;
const url = 'https://example.com/download.zip';
const item = () => ({ url, startTime: new Date(now).toISOString() });
function request(observer, requestId, method = 'GET', responded = true) {
    const details = { requestId, url, method, type: 'main_frame' };
    observer.before(details);
    observer.headers({ ...details, requestHeaders: [{ name: 'Cookie', value: `request=${requestId}` }] });
    if (responded) observer.response({ ...details, statusCode: 200 });
}
try {
    const sequential = new RequestObserver();
    request(sequential, 'first');
    assert.ok(sequential.claim(item()));
    assert.equal(sequential.claim(item()), null, 'A consumed request cannot be reused');
    request(sequential, 'second');
    assert.equal(sequential.claim(item()).headers[0].value, 'request=second');

    // Stale requests leftover (> 10s window)
    const stale = new RequestObserver();
    request(stale, 'old-unclaimed');
    now += 11000; // 11 seconds: outside regular 10s window
    request(stale, 'new');
    assert.equal(stale.claim(item()).headers[0].value, 'request=new');

    // Single request diagnostic reasons (must NOT say '多个近期未处理请求')
    const singlePost = new RequestObserver();
    request(singlePost, 'post-only', 'POST', true);
    assert.equal(singlePost.claim(item()), null);
    assert.match(singlePost.lastReason, /仅支持自动接管 GET 请求，当前为 POST/);

    const singleUnres = new RequestObserver();
    request(singleUnres, 'unres-only', 'GET', false);
    assert.equal(singleUnres.claim(item()), null);
    assert.match(singleUnres.lastReason, /网络请求尚未取得响应/);

    const single404 = new RequestObserver();
    const d404 = { requestId: 's404', url, method: 'GET', type: 'main_frame' };
    single404.before(d404);
    single404.headers({ ...d404, requestHeaders: [] });
    single404.response({ ...d404, statusCode: 404 });
    assert.equal(single404.claim(item()), null);
    assert.match(single404.lastReason, /网络响应状态异常（404）/);

    const singleAuth = new RequestObserver();
    const dAuth = { requestId: 'sAuth', url, method: 'GET', type: 'main_frame' };
    singleAuth.before(dAuth);
    singleAuth.headers({ ...dAuth, requestHeaders: [{ name: 'Authorization', value: 'Bearer token' }] });
    singleAuth.response({ ...dAuth, statusCode: 200 });
    assert.equal(singleAuth.claim(item()), null);
    assert.match(singleAuth.lastReason, /请求包含认证信息/);

    const singleRange = new RequestObserver();
    const dRange = { requestId: 'sRange', url, method: 'GET', type: 'main_frame' };
    singleRange.before(dRange);
    singleRange.headers({ ...dRange, requestHeaders: [{ name: 'Range', value: 'bytes=0-100' }] });
    singleRange.response({ ...dRange, statusCode: 200 });
    assert.equal(singleRange.claim(item()), null);
    assert.match(singleRange.lastReason, /请求包含范围请求头/);

    // Ambiguous concurrent requests: different cookies, POST, unresponded
    for (const [method, responded, pattern] of [
        ['GET', true, /关键请求头或参数不一致/],
        ['POST', true, /无法排除的 POST 或非 GET 请求/],
        ['GET', false, /尚未取得响应的请求/]
    ]) {
        const concurrent = new RequestObserver();
        request(concurrent, 'one');
        request(concurrent, 'two', method, responded);
        assert.equal(concurrent.claim(item()), null, 'Do not guess among concurrent requests');
        assert.match(concurrent.lastReason, /同一地址存在多个近期未处理请求/);
        assert.match(concurrent.lastReason, pattern);
    }

    // areEquivalent checks originalUrl
    const req1 = { url, originalUrl: 'https://example.com/start1', method: 'GET', responseTime: now, initiator: 'null', headers: [] };
    const req2 = { url, originalUrl: 'https://example.com/start2', method: 'GET', responseTime: now, initiator: 'null', headers: [] };
    assert.equal((await import('../undone_plugin/js/request_observer.js')).areEquivalent(req1, req2), false, 'Different originalUrl must not be equivalent');

    // Equivalent concurrent requests: same cookie/key headers should be claimable sequentially
    const equiv = new RequestObserver();
    const details1 = { requestId: 'eq1', url, method: 'GET', type: 'main_frame' };
    equiv.before(details1);
    equiv.headers({ ...details1, requestHeaders: [{ name: 'Cookie', value: 'session=abc' }, { name: 'User-Agent', value: 'ua' }] });
    equiv.response({ ...details1, statusCode: 200 });

    const details2 = { requestId: 'eq2', url, method: 'GET', type: 'main_frame' };
    equiv.before(details2);
    equiv.headers({ ...details2, requestHeaders: [{ name: 'Cookie', value: 'session=abc' }, { name: 'User-Agent', value: 'ua' }] });
    equiv.response({ ...details2, statusCode: 200 });

    const c1 = equiv.claim(item());
    assert.ok(c1, 'First equivalent request claimed');
    assert.equal(c1.headers[0].value, 'session=abc');
    const c2 = equiv.claim(item());
    assert.ok(c2, 'Second equivalent request claimed');
    assert.equal(c2.headers[0].value, 'session=abc');
    assert.equal(equiv.claim(item()), null, 'No remaining unconsumed requests');

    // Slow response: unique candidate within 15s extended window (e.g. at 12s)
    const slow = new RequestObserver();
    request(slow, 'slow-unique');
    now += 12000; // 12 seconds: > 10s, <= 15s
    const slowClaimed = slow.claim(item());
    assert.ok(slowClaimed, 'Unique slow candidate within 15s should be claimed');
    assert.equal(slowClaimed.headers[0].value, 'request=slow-unique');

    // Expired requests (> 15s)
    const expired = new RequestObserver();
    request(expired, 'expired');
    now += 16000;
    assert.equal(expired.claim(item()), null);
    assert.match(expired.lastReason, /时间不匹配/);

    // Relaxed request types: xmlhttprequest, media, etc.
    for (const type of ['xmlhttprequest', 'media', 'other', 'main_frame', 'sub_frame']) {
        const typeObs = new RequestObserver();
        const typeDetails = { requestId: 't-' + type, url, method: 'GET', type };
        typeObs.before(typeDetails);
        typeObs.headers({ ...typeDetails, requestHeaders: [{ name: 'Cookie', value: 'test' }] });
        typeObs.response({ ...typeDetails, statusCode: 200 });
        assert.ok(typeObs.claim(item()), `Type ${type} should be accepted`);
    }

    // Redirects: GET -> GET accepted
    const getRedirect = new RequestObserver();
    getRedirect.before({ requestId: 'r-get', url: 'http://example.com/initial.zip', method: 'GET', type: 'main_frame' });
    getRedirect.before({ requestId: 'r-get', url, method: 'GET', type: 'main_frame' });
    getRedirect.headers({ requestId: 'r-get', url, requestHeaders: [{ name: 'Cookie', value: 'redir' }] });
    getRedirect.response({ requestId: 'r-get', url, statusCode: 200 });
    const claimedRedir = getRedirect.claim({ url: 'http://example.com/initial.zip', finalUrl: url, startTime: new Date(now).toISOString() });
    assert.ok(claimedRedir, 'GET->GET redirect should be claimed');

    // Redirects: POST -> GET rejected
    const postRedirect = new RequestObserver();
    postRedirect.before({ requestId: 'r-post', url: 'http://example.com/login', method: 'POST', type: 'main_frame' });
    postRedirect.before({ requestId: 'r-post', url, method: 'GET', type: 'main_frame' });
    postRedirect.headers({ requestId: 'r-post', url, requestHeaders: [{ name: 'Cookie', value: 'redir' }] });
    postRedirect.response({ requestId: 'r-post', url, statusCode: 200 });
    assert.equal(postRedirect.claim({ url: 'http://example.com/login', finalUrl: url, startTime: new Date(now).toISOString() }), null, 'POST->GET redirect must be rejected');
    assert.match(postRedirect.lastReason, /仅支持自动接管源自 GET 的请求（当前为 POST 重定向）/);

    // Redirects: POST -> POST rejected with redirect diagnosis
    const postPostRedirect = new RequestObserver();
    postPostRedirect.before({ requestId: 'r-post-post', url: 'http://example.com/login', method: 'POST', type: 'main_frame' });
    postPostRedirect.before({ requestId: 'r-post-post', url, method: 'POST', type: 'main_frame' });
    postPostRedirect.headers({ requestId: 'r-post-post', url, requestHeaders: [{ name: 'Cookie', value: 'redir' }] });
    postPostRedirect.response({ requestId: 'r-post-post', url, statusCode: 200 });
    assert.equal(postPostRedirect.claim({ url: 'http://example.com/login', finalUrl: url, startTime: new Date(now).toISOString() }), null, 'POST->POST redirect must be rejected');
    assert.match(postPostRedirect.lastReason, /仅支持自动接管源自 GET 的请求（当前为 POST 重定向）/);

    // Redirects: GET -> POST rejected as non-GET method
    const getPostRedirect = new RequestObserver();
    getPostRedirect.before({ requestId: 'r-get-post', url: 'http://example.com/start', method: 'GET', type: 'main_frame' });
    getPostRedirect.before({ requestId: 'r-get-post', url, method: 'POST', type: 'main_frame' });
    getPostRedirect.headers({ requestId: 'r-get-post', url, requestHeaders: [{ name: 'Cookie', value: 'redir' }] });
    getPostRedirect.response({ requestId: 'r-get-post', url, statusCode: 200 });
    assert.equal(getPostRedirect.claim({ url: 'http://example.com/start', finalUrl: url, startTime: new Date(now).toISOString() }), null, 'GET->POST redirect must be rejected');
    assert.match(getPostRedirect.lastReason, /仅支持自动接管 GET 请求，当前为 POST/);

    // Hash fragment handling in download items
    const hashObs = new RequestObserver();
    request(hashObs, 'hash-req');
    const claimedHash = hashObs.claim({ url: url + '#fragment', finalUrl: url + '#fragment', startTime: new Date(now).toISOString() });
    assert.ok(claimedHash, 'Download item with URL fragment should match observed request');
    assert.equal(claimedHash.headers[0].value, 'request=hash-req');

    // Redirects isolation: unrelated request starting from same initial URL but redirecting elsewhere must NOT cause false ambiguity
    const isolateObs = new RequestObserver();
    // Unrelated API request starting at initial.zip but redirecting to error page
    isolateObs.before({ requestId: 'r-unrelated', url: 'http://example.com/initial.zip', method: 'GET', type: 'xmlhttprequest' });
    isolateObs.before({ requestId: 'r-unrelated', url: 'http://example.com/error', method: 'GET', type: 'xmlhttprequest' });
    isolateObs.headers({ requestId: 'r-unrelated', url: 'http://example.com/error', requestHeaders: [{ name: 'Cookie', value: 'err' }] });
    isolateObs.response({ requestId: 'r-unrelated', url: 'http://example.com/error', statusCode: 200 });
    // Real download request starting at initial.zip and redirecting to final download url
    isolateObs.before({ requestId: 'r-real', url: 'http://example.com/initial.zip', method: 'GET', type: 'main_frame' });
    isolateObs.before({ requestId: 'r-real', url, method: 'GET', type: 'main_frame' });
    isolateObs.headers({ requestId: 'r-real', url, requestHeaders: [{ name: 'Cookie', value: 'real' }] });
    isolateObs.response({ requestId: 'r-real', url, statusCode: 200 });
    const claimedReal = isolateObs.claim({ url: 'http://example.com/initial.zip', finalUrl: url, startTime: new Date(now).toISOString() });
    assert.ok(claimedReal, 'Unrelated redirect must not create false ambiguity');
    assert.equal(claimedReal.headers[0].value, 'real');

    // Disambiguation: two requests to same finalUrl from different initial URLs
    const disambigObs = new RequestObserver();
    disambigObs.before({ requestId: 'r-start1', url: 'http://mirror1.com/dl', method: 'GET', type: 'main_frame' });
    disambigObs.before({ requestId: 'r-start1', url, method: 'GET', type: 'main_frame' });
    disambigObs.headers({ requestId: 'r-start1', url, requestHeaders: [{ name: 'Cookie', value: 'm1' }] });
    disambigObs.response({ requestId: 'r-start1', url, statusCode: 200 });

    disambigObs.before({ requestId: 'r-start2', url: 'http://mirror2.com/dl', method: 'GET', type: 'main_frame' });
    disambigObs.before({ requestId: 'r-start2', url, method: 'GET', type: 'main_frame' });
    disambigObs.headers({ requestId: 'r-start2', url, requestHeaders: [{ name: 'Cookie', value: 'm2' }] });
    disambigObs.response({ requestId: 'r-start2', url, statusCode: 200 });

    const cMirror1 = disambigObs.claim({ url: 'http://mirror1.com/dl', finalUrl: url, startTime: new Date(now).toISOString() });
    assert.ok(cMirror1, 'Mirror 1 download must match Mirror 1 request');
    assert.equal(cMirror1.headers[0].value, 'm1');

    const cMirror2 = disambigObs.claim({ url: 'http://mirror2.com/dl', finalUrl: url, startTime: new Date(now).toISOString() });
    assert.ok(cMirror2, 'Mirror 2 download must match Mirror 2 request');
    assert.equal(cMirror2.headers[0].value, 'm2');

    // Subpath trailing slash isolation in claim: request for /file/ must not be claimed by /file
    const slashObs = new RequestObserver();
    const dSlash = { requestId: 'slash-req', url: 'https://example.com/api/resource/', method: 'GET', type: 'main_frame' };
    slashObs.before(dSlash);
    slashObs.headers({ ...dSlash, requestHeaders: [{ name: 'Cookie', value: 'res-slash' }] });
    slashObs.response({ ...dSlash, statusCode: 200 });
    assert.equal(slashObs.claim({ url: 'https://example.com/api/resource', startTime: new Date(now).toISOString() }), null, 'Different path trailing slash must not be claimed');
    const claimedExact = slashObs.claim({ url: 'https://example.com/api/resource/', startTime: new Date(now).toISOString() });
    assert.ok(claimedExact, 'Exact path with trailing slash claimed');
    assert.equal(claimedExact.headers[0].value, 'res-slash');

    // URL normalization in urlMatch
    const { urlMatch } = await import('../undone_plugin/js/request_observer.js');
    assert.ok(urlMatch('http://example.com', 'http://example.com/'), 'Root slash normalized');
    assert.equal(urlMatch('http://example.com/dir', 'http://example.com/dir/'), false, 'Subpath trailing slash must not be ignored');
    assert.equal(urlMatch('http://example.com/file', 'http://example.com/file/'), false, 'Different resource with trailing slash must not match');
    assert.ok(urlMatch('http://example.com/file.zip#dl', 'http://example.com/file.zip'), 'URL fragment stripped in match');
    assert.ok(urlMatch('http://EXAMPLE.com/path', 'http://example.com/path'), 'Host casing normalized');
    assert.ok(urlMatch('http://example.com:80/path', 'http://example.com/path'), 'Default port normalized');
    assert.equal(urlMatch('http://example.com/a', 'http://example.com/b'), false, 'Different paths not matched');

    console.log('PASS repeated URLs, header ownership, stale responses, concurrent ambiguity, equivalent concurrency, slow responses, relaxed types, redirects, URL normalization, redirect isolation');
} finally { Date.now = realNow; }

