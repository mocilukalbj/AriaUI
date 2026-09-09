import { nativeClient } from './js/ariaui_native.js';
import { defaults, validateSettings, captureSkipReason, makePayload } from './js/settings.js';
import { RequestObserver } from './js/request_observer.js';
import { HandoffManager } from './js/handoff.js';
import { buildLinkContextPayload } from './js/context_menu.js';

let settings = { ...defaults };
const observer = new RequestObserver();
const capturing = new Set();
const manager = new HandoffManager(nativeClient, async record => {
    await chrome.notifications.create(`handoff:${record.requestId}`, {
        type: 'basic', iconUrl: 'images/logo128.png', title: 'AriaUI：交接需要确认',
        message: '请打开扩展查看记录。结果未知时，浏览器下载可能仍处于暂停状态。'
    }).catch(() => {});
    await updateBadge();
});

const ready = (async () => {
    if (chrome?.storage?.local?.setAccessLevel) {
        await chrome.storage.local.setAccessLevel({ accessLevel: 'TRUSTED_CONTEXTS' }).catch(() => {});
    }
    const stored = await chrome.storage.local.get('companionSettings');
    if (stored.companionSettings) settings = validateSettings(stored.companionSettings);
    await manager.load();
})();

function guard(promise) {
    void promise.catch(() => {
        // No payloads or native error messages in logs/notifications.
        void chrome.action.setBadgeText({ text: '!' }).catch(() => {});
        void chrome.notifications.create('companion-error', {
            type: 'basic', iconUrl: 'images/logo128.png', title: 'AriaUI：操作未完成',
            message: '请检查扩展交接记录及浏览器下载列表；确认状态前不要重复发送。'
        }).catch(() => {});
    });
}

async function updateBadge() {
    const count = [...manager.records.values()].filter(r => ['unknown', 'attention'].includes(r.stage)).length;
    await chrome.action.setBadgeText({ text: count ? String(count) : settings.enabled ? 'ON' : '' });
    await chrome.action.setBadgeBackgroundColor({ color: count ? '#a44b12' : '#14675a' });
}

async function menus() {
    await chrome.contextMenus.removeAll().catch(() => {});
    // Context menu entries: context-aware download (with Cookie/Referer) and plain send
    chrome.contextMenus.create({ id: 'send-link-context', title: '用 AriaUI 下载（携带 Cookie / Referer）', contexts: ['link'] }, () => void chrome.runtime.lastError);
    chrome.contextMenus.create({ id: 'send-link', title: '仅发送链接到 AriaUI', contexts: ['link'] }, () => void chrome.runtime.lastError);
    chrome.contextMenus.create({ id: 'toggle', title: '自动接管下载', type: 'checkbox', checked: settings.enabled, contexts: ['action'] }, () => void chrome.runtime.lastError);
    chrome.contextMenus.create({ id: 'settings', title: '设置与交接记录', contexts: ['action'] }, () => void chrome.runtime.lastError);
}

async function saveSettings(value) {
    const next = validateSettings(value);
    await chrome.storage.local.set({ companionSettings: next });
    settings = next;
    await menus();
    await updateBadge();
}

// Register synchronously so MV3 can dispatch events when starting a fresh worker.
const filter = { urls: ['http://*/*', 'https://*/*'] };
chrome.webRequest.onBeforeRequest.addListener(details => observer.before(details), filter);
chrome.webRequest.onSendHeaders.addListener(details => observer.headers(details), filter, ['requestHeaders', 'extraHeaders']);
chrome.webRequest.onResponseStarted.addListener(details => observer.response(details), filter);

chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    let released = false;
    const release = () => { if (!released) { released = true; suggest(); } };
    if (capturing.has(item.id)) { release(); return false; }
    capturing.add(item.id);
    guard((async () => {
        try {
            await ready;
            const skip = captureSkipReason(item, settings);
            const report = reason => chrome.storage.session.set({ lastCapture: { time: Date.now(), reason } });
            if (skip) { await report(skip); return; }
            const request = observer.claim(item);
            if (!request) { await report(observer.lastReason); return; }
            let payload;
            try { payload = makePayload(item.finalUrl || item.url, item.filename, request.headers); }
            catch { await report('下载地址、文件名或请求头无法交接。'); return; }
            await report('已尝试交接，请查看下方交接记录。');
            await manager.capture(item, payload, release);
            await updateBadge();
        } finally { release(); capturing.delete(item.id); }
    })());
    return true;
});

chrome.action.onClicked.addListener(() => guard(chrome.runtime.openOptionsPage()));
chrome.notifications.onClicked.addListener(() => guard(chrome.runtime.openOptionsPage()));
chrome.commands.onCommand.addListener(command => {
    if (command === 'toggle-capture') guard(ready.then(() => saveSettings({ ...settings, enabled: !settings.enabled })));
});
chrome.contextMenus.onClicked.addListener((info, tab) => guard((async () => {
    await ready;
    if (tab?.incognito) return;
    if (info.menuItemId === 'toggle') await saveSettings({ ...settings, enabled: info.checked });
    if (info.menuItemId === 'settings') await chrome.runtime.openOptionsPage();
    if (info.menuItemId === 'send-link-context') {
        try {
            const payload = await buildLinkContextPayload(info, tab);
            const record = await manager.manual(payload);
            if (record.reason !== 'Sent') {
                const hint = record.reason === 'NotSubmitted'
                    ? '未提交到 AriaUI，请检查宿主连接及主程序运行状态。'
                    : `发送未被接纳（${record.reason}），请查看扩展交接记录。`;
                await chrome.notifications.create(`context-err:${record.requestId}`, {
                    type: 'basic', iconUrl: 'images/logo128.png', title: 'AriaUI：任务未完成',
                    message: hint
                }).catch(() => {});
            }
            await updateBadge();
        } catch (err) {
            await chrome.notifications.create(`context-fail:${Date.now()}`, {
                type: 'basic', iconUrl: 'images/logo128.png', title: 'AriaUI：无法发送链接',
                message: err?.message || '构建下载凭据或发送失败。'
            }).catch(() => {});
        }
    }
    if (info.menuItemId === 'send-link') {
        try {
            // Explicit link sending creates a GET, without borrowing cookies from another context.
            const record = await manager.manual(makePayload(info.linkUrl));
            if (record.reason !== 'Sent') {
                const hint = record.reason === 'NotSubmitted'
                    ? '未提交到 AriaUI，请检查宿主连接及主程序运行状态。'
                    : `发送未被接纳（${record.reason}），请查看扩展交接记录。`;
                await chrome.notifications.create(`link-err:${record.requestId}`, {
                    type: 'basic', iconUrl: 'images/logo128.png', title: 'AriaUI：任务未完成',
                    message: hint
                }).catch(() => {});
            }
            await updateBadge();
        } catch (err) {
            await chrome.notifications.create(`link-fail:${Date.now()}`, {
                type: 'basic', iconUrl: 'images/logo128.png', title: 'AriaUI：无法发送链接',
                message: err?.message || '链接格式不支持。'
            }).catch(() => {});
        }
    }
})()));

chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL('options.html')) return false;
    (async () => {
        await ready;
        switch (message?.action) {
            case 'state': return { settings, records: [...manager.records.values()].sort((a, b) => b.created - a.created),
                extensionId: chrome.runtime.id, lastCapture: (await chrome.storage.session.get('lastCapture')).lastCapture };
            case 'settings': await saveSettings(message.value); return {};
            case 'diagnose': return nativeClient.diagnose();
            case 'send': return { record: await manager.manual(makePayload(message.url)) };
            case 'resolve': await manager.resolve(message.requestId, message.decision); await updateBadge(); return {};
            default: throw new Error('UnknownAction');
        }
    })().then(data => respond({ ok: true, ...data }), err => respond({ ok: false,
        error: err?.message || '操作未完成：请检查输入、宿主连接及交接记录；不要重复发送待确认任务。' }));
    return true;
});

chrome.alarms.onAlarm.addListener(alarm => {
    if (alarm.name === 'recover') guard(ready.then(async () => {
        observer.prune(); await manager.recover(); await updateBadge();
    }));
});
chrome.runtime.onInstalled?.addListener(() => guard(ready.then(menus)));
guard(ready.then(async () => {
    await menus();
    await chrome.alarms.create('recover', { periodInMinutes: 1 });
    await manager.recover();
    await updateBadge();
}));
