export const defaults = Object.freeze({ enabled: false, minMiB: 0, captureUnknownSize: true,
    allowedSites: [], blockedSites: [], allowedExts: [], blockedExts: [] });

export function validateSettings(input) {
    const value = { ...defaults };
    if (input.enabled !== undefined) value.enabled = input.enabled === true;
    if (input.captureUnknownSize !== undefined) value.captureUnknownSize = input.captureUnknownSize === true;
    const size = Number(input.minMiB ?? defaults.minMiB);
    if (!Number.isFinite(size) || size < 0) throw new Error('大小必须是非负数');
    value.minMiB = size;
    for (const key of ['allowedSites', 'blockedSites', 'allowedExts', 'blockedExts']) {
        const arr = input[key] ?? defaults[key];
        if (!Array.isArray(arr) || arr.length > 200) throw new Error('每组规则最多 200 条');
        value[key] = [...new Set(arr.map(x => String(x).trim().toLowerCase()).filter(Boolean))];
        if (value[key].some(x => x.length > 253 || /[\s/\\:]/.test(x))) throw new Error('规则只能填写域名或扩展名');
    }
    return value;
}

export function wildcard(value, pattern) {
    const escaped = pattern.split('*').map(x => x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('.*');
    return new RegExp(`^${escaped}$`, 'i').test(value);
}

export function shouldCapture(item, settings) { return captureSkipReason(item, settings) === null; }

export function captureSkipReason(item, settings) {
    if (!settings.enabled) return '自动接管未开启：勾选后请点击保存设置。';
    if (item.incognito || item.byExtensionId) return '跳过无痕或其他扩展创建的下载。';
    if (item.paused || item.state !== 'in_progress' || item.error) return '下载已暂停、结束或出错。';
    if (item.danger && item.danger !== 'safe') return '浏览器尚未将下载标记为安全。';
    let url;
    try { url = new URL(item.finalUrl || item.url); } catch { return '下载地址无法解析。'; }
    if (['blob:', 'data:'].includes(url.protocol)) return 'Blob 或 Data 链接需由浏览器处理，无法通过网络请求接管。';
    if (!['http:', 'https:'].includes(url.protocol)) return '仅自动接管普通 HTTP / HTTPS 下载。';
    if (url.username || url.password) return '下载地址包含嵌入认证信息，暂未自动接管。';
    const name = (item.filename || url.pathname).split(/[\\/]/).pop().toLowerCase();
    const siteMatches = rules => rules.some(rule => wildcard(url.hostname, rule));
    const extMatches = rules => rules.some(rule => rule === '*' || name.endsWith('.' + rule.replace(/^\./, '')));
    if (siteMatches(settings.blockedSites) || extMatches(settings.blockedExts)) return '下载命中了排除域名或扩展名规则。';
    if (settings.allowedSites.length && !siteMatches(settings.allowedSites)) return '下载域名不在允许列表中。';
    if (settings.allowedExts.length && !extMatches(settings.allowedExts)) return '文件扩展名不在允许列表中。';
    const size = item.totalBytes >= 0 ? item.totalBytes : item.fileSize;
    if (!(size >= 0)) return settings.captureUnknownSize ? null : '下载大小未知：如需接管，请勾选允许接管大小未知的下载并保存。';
    return size >= settings.minMiB * 1048576 ? null : '下载大小低于设置的最小大小。';
}

export function makePayload(urlText, filename = '', headers = []) {
    let url;
    try { url = new URL(urlText); } catch {
        const err = new Error('下载地址无法解析为有效 URL。');
        err.code = 'InvalidURL';
        throw err;
    }
    if (!['http:', 'https:', 'magnet:'].includes(url.protocol) || url.username || url.password) {
        const err = new Error('仅支持普通 HTTP、HTTPS 或 magnet 链接，不支持嵌入认证信息。');
        err.code = 'UnsupportedURL';
        throw err;
    }
    if (url.protocol === 'magnet:' && !url.searchParams.get('xt')) {
        const err = new Error('无效的 magnet 链接（缺少 xt 参数）。');
        err.code = 'InvalidMagnet';
        throw err;
    }
    const clean = text => {
        if (/[\r\n\0]/.test(text) || new TextEncoder().encode(text).length > 8192) {
            const err = new Error('参数包含非法控制字符或单项长度超出限制（8KB）。');
            err.code = 'InvalidField';
            throw err;
        }
        return text;
    };
    const payload = { url: clean(url.href) };
    const rawName = (filename || '').split(/[\\/]/).pop();
    const name = rawName ? rawName.replace(/\.{2,}/g, '.') : '';
    if (name && name !== '.' && name !== '..' && !name.includes('..')) payload.out = clean(name);
    if (url.protocol === 'magnet:') return payload;
    const forwarded = [];
    for (const header of headers) {
        const key = header.name.toLowerCase();
        if (typeof header.value !== 'string') continue;
        const value = clean(header.value);
        if (key === 'referer') payload.referer = value;
        if (key === 'user-agent') payload.userAgent = value;
        if (['cookie', 'origin', 'accept', 'accept-language'].includes(key)) {
            const line = `${header.name}: ${value}`;
            if (new TextEncoder().encode(line).length > 8192) {
                const err = new Error('请求头单项长度超出限制（8KB）。');
                err.code = 'InvalidField';
                throw err;
            }
            forwarded.push(line);
        }
    }
    if (forwarded.length > 32) {
        const err = new Error('转发请求头数量超出限制（最多 32 个）。');
        err.code = 'TooManyHeaders';
        throw err;
    }
    if (forwarded.length) payload.headers = forwarded;
    return payload;
}
