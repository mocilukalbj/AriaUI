import assert from 'node:assert/strict';
import { defaults, validateSettings, captureSkipReason, wildcard, shouldCapture, makePayload } from '../undone_plugin/js/settings.js';

// 1. Defaults verification
assert.equal(defaults.enabled, false);
assert.equal(defaults.captureUnknownSize, true);
assert.equal(defaults.minMiB, 0);
assert.deepEqual(defaults.allowedSites, []);
assert.deepEqual(defaults.blockedSites, []);
assert.deepEqual(defaults.allowedExts, []);
assert.deepEqual(defaults.blockedExts, []);

// 2. Existing settings preservation
const existingSettings = validateSettings({
    enabled: true,
    captureUnknownSize: false,
    minMiB: 10,
    blockedSites: ['example.com']
});
assert.equal(existingSettings.captureUnknownSize, false, 'Explicitly false captureUnknownSize must not be silently overwritten');
assert.equal(existingSettings.minMiB, 10);
assert.deepEqual(existingSettings.blockedSites, ['example.com']);

// New install / undefined property gets new default
const newInstallSettings = validateSettings({});
assert.equal(newInstallSettings.captureUnknownSize, true);

// 3. Skip reason diagnostics
const baseItem = {
    id: 1,
    url: 'https://example.com/file.zip',
    filename: 'C:\\Downloads\\file.zip',
    state: 'in_progress',
    danger: 'safe',
    paused: false,
    totalBytes: 10485760 // 10 MiB
};

const activeSettings = validateSettings({ enabled: true });

// Normal valid item
assert.equal(captureSkipReason(baseItem, activeSettings), null);
assert.equal(shouldCapture(baseItem, activeSettings), true);

// Disabled setting
assert.match(captureSkipReason(baseItem, defaults), /自动接管未开启/);

// Blob link distinction
assert.match(
    captureSkipReason({ ...baseItem, url: 'blob:https://example.com/uuid' }, activeSettings),
    /Blob 或 Data 链接需由浏览器处理/
);

// Data URL distinction
assert.match(
    captureSkipReason({ ...baseItem, url: 'data:application/octet-stream;base64,AAAA' }, activeSettings),
    /Blob 或 Data 链接需由浏览器处理/
);

// Other non-HTTP/HTTPS protocol
assert.match(
    captureSkipReason({ ...baseItem, url: 'ftp://example.com/file.zip' }, activeSettings),
    /仅自动接管普通 HTTP \/ HTTPS 下载/
);

// Unknown size when captureUnknownSize is true
const unknownSizeItem = { ...baseItem, totalBytes: -1, fileSize: -1 };
assert.equal(captureSkipReason(unknownSizeItem, activeSettings), null);

// Unknown size when captureUnknownSize is false
const strictSettings = validateSettings({ enabled: true, captureUnknownSize: false });
assert.match(captureSkipReason(unknownSizeItem, strictSettings), /下载大小未知/);

// Size threshold
const sizeLimitSettings = validateSettings({ enabled: true, minMiB: 20 });
assert.match(captureSkipReason(baseItem, sizeLimitSettings), /下载大小低于设置的最小大小/);

// Blocked site / ext
const blockSiteSettings = validateSettings({ enabled: true, blockedSites: ['*.example.com', 'example.com'] });
assert.match(captureSkipReason(baseItem, blockSiteSettings), /下载命中了排除域名或扩展名规则/);

const blockExtSettings = validateSettings({ enabled: true, blockedExts: ['zip'] });
assert.match(captureSkipReason(baseItem, blockExtSettings), /下载命中了排除域名或扩展名规则/);

// Wildcard matching
assert.ok(wildcard('test.example.com', '*.example.com'));
assert.ok(!wildcard('example.com', '*.example.com'));
assert.ok(wildcard('file.tar.gz', '*.gz'));

// 4. makePayload tests
const p1 = makePayload('https://example.com/downloads/setup.exe', 'setup.exe', [{ name: 'User-Agent', value: 'ua' }]);
assert.equal(p1.url, 'https://example.com/downloads/setup.exe');
assert.equal(p1.out, 'setup.exe');
assert.equal(p1.userAgent, 'ua');

const pMagnet = makePayload('magnet:?xt=urn:btih:1234567890abcdef&dn=arch');
assert.equal(pMagnet.url, 'magnet:?xt=urn:btih:1234567890abcdef&dn=arch');

assert.throws(() => makePayload('not-a-url'), /下载地址无法解析为有效 URL/);
assert.throws(() => makePayload('ftp://example.com/file'), /仅支持普通 HTTP/);
assert.throws(() => makePayload('https://user:pass@example.com/file'), /不支持嵌入认证信息/);
assert.throws(() => makePayload('magnet:?dn=test'), /无效的 magnet 链接/);

const pNull = makePayload('https://example.com/file.zip', null);
assert.equal(pNull.url, 'https://example.com/file.zip');
assert.equal(pNull.out, undefined, 'Null filename should not throw and should leave out undefined');

const pDots = makePayload('https://example.com/file.zip', 'foo..bar.zip');
assert.equal(pDots.out, 'foo.bar.zip', 'Consecutive dots in filename should be sanitized');

const pOnlyDots = makePayload('https://example.com/file.zip', '..');
assert.equal(pOnlyDots.out, undefined, 'Double dot only filename should be omitted');

console.log('PASS settings defaults, preservation, unknown size handling, blob/data diagnostics, wildcard filtering, makePayload');
