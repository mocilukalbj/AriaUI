export const defaults = Object.freeze({ enabled: false, minMiB: 0, captureUnknownSize: false,
    allowedSites: [], blockedSites: [], allowedExts: [], blockedExts: [] });

export function validateSettings(input) {
    const value = { ...defaults };
    for (const key of ['enabled', 'captureUnknownSize']) value[key] = input[key] === true;
    const size = Number(input.minMiB ?? 0);
    if (!Number.isFinite(size) || size < 0) throw new Error('大小必须是非负数');
    value.minMiB = size;
    for (const key of ['allowedSites', 'blockedSites', 'allowedExts', 'blockedExts']) {
        if (!Array.isArray(input[key]) || input[key].length > 200) throw new Error('每组规则最多 200 条');
        value[key] = [...new Set(input[key].map(x => String(x).trim().toLowerCase()).filter(Boolean))];
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
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) return '仅自动接管普通 HTTP / HTTPS 下载，blob 等链接需由浏览器处理。';
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
    const url = new URL(urlText);
    if (!['http:', 'https:', 'magnet:'].includes(url.protocol) || url.username || url.password) throw new Error('UnsupportedURL');
    if (url.protocol === 'magnet:' && !url.searchParams.get('xt')) throw new Error('InvalidMagnet');
    const clean = text => {
        if (/[\r\n\0]/.test(text) || new TextEncoder().encode(text).length > 8192) throw new Error('InvalidField');
        return text;
    };
    const payload = { url: clean(url.href) };
    const name = filename.split(/[\\/]/).pop();
    if (name && name !== '.' && name !== '..') payload.out = clean(name);
    if (url.protocol === 'magnet:') return payload;
    const forwarded = [];
    for (const header of headers) {
        const key = header.name.toLowerCase();
        if (typeof header.value !== 'string') continue;
        const value = clean(header.value);
        if (key === 'referer') payload.referer = value;
        if (key === 'user-agent') payload.userAgent = value;
        if (['cookie', 'origin', 'accept', 'accept-language'].includes(key)) forwarded.push(`${header.name}: ${value}`);
    }
    if (forwarded.length > 32) throw new Error('TooManyHeaders');
    if (forwarded.length) payload.headers = forwarded;
    return payload;
}
