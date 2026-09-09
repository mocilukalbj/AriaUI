const $ = id => document.getElementById(id);
const ruleKeys = ['allowedSites', 'blockedSites', 'allowedExts', 'blockedExts'];
const stages = { prepared: '准备中', paused: '浏览器已暂停', submitting: '正在交接', unknown: '结果待确认',
    accepted: 'AriaUI 已接纳，正在处理浏览器下载', rejected: '正在恢复浏览器', attention: '需要处理', done: '已结束' };
const reasons = { Sent: '已交给 AriaUI', NotSubmitted: '未提交，保留浏览器下载', PauseFailed: '未能暂停浏览器',
    Disconnected: '连接中断，接管结果未知', Pending: 'AriaUI 仍在处理', Failed: '宿主返回执行异常，接管结果待确认',
    UnknownOutcome: '找不到原请求结果', InstanceMismatch: 'AriaUI 实例已变化，无法确认旧请求',
    CancelFailed: 'AriaUI 已接纳，但取消浏览器下载失败', ResumeFailed: '恢复失败，请在浏览器下载列表处理',
    ResumeInBrowser: '下载已中断，请在浏览器下载列表处理', BrowserWasResumed: 'AriaUI 已接纳，但浏览器下载已被继续',
    BrowserAlreadyCompleted: '浏览器下载已完成，请检查 AriaUI 是否重复', BrowserItemMissing: '原浏览器下载记录已不存在',
    AcceptedBrowserItemMissing: 'AriaUI 已接纳，原浏览器记录已不存在', UserContinuedBrowser: '已选择继续浏览器',
    UserResolved: '已标记为处理完毕', QueueFull: 'AriaUI 队列已满，未接纳', RateLimited: '请求过于频繁，未接纳',
    BadRequest: '请求被拒绝', InvalidPath: '文件名或路径被拒绝', HeaderInjection: '请求头被拒绝',
    UnsupportedScheme: '链接类型不支持', UnsupportedVersion: '协议版本不支持', UnsupportedAction: '操作不支持',
    FrameTooLarge: '数据帧超出限制（64KB）' };

async function rpc(message) {
    const result = await chrome.runtime.sendMessage(message);
    if (!result?.ok) throw new Error(result?.error || '扩展后台未响应');
    return result;
}
async function run(button, work) {
    if (button) button.disabled = true;
    $('feedback').textContent = '';
    try { await work(); }
    catch (error) { $('feedback').textContent = error.message; }
    finally { if (button) button.disabled = false; }
}
async function refresh(fillSettings = false) {
    const state = await rpc({ action: 'state' });
    $('extension-id').textContent = state.extensionId;
    $('last-capture').textContent = state.lastCapture
        ? `最近一次接管检查（${new Date(state.lastCapture.time).toLocaleString()}）：${state.lastCapture.reason}`
        : '尚未观察到下载事件。请检查扩展的网站访问权限，发起一次新下载后点击刷新。';
    if (fillSettings) {
        for (const key of ruleKeys) $(key).value = state.settings[key].join('\n');
        $('enabled').checked = state.settings.enabled;
        $('captureUnknownSize').checked = state.settings.captureUnknownSize;
        $('minMiB').value = state.settings.minMiB;
    }
    $('records').replaceChildren();
    if (!state.records.length) $('records').textContent = '还没有交接记录。';
    for (const record of state.records) {
        const div = document.createElement('div'); div.className = 'record';
        const title = document.createElement('strong'); title.textContent = `${record.label || '浏览器下载'} · ${stages[record.stage] || record.stage}`;
        const detail = document.createElement('p'); detail.textContent = reasons[record.reason] || record.reason || '等待处理';
        const meta = document.createElement('p'); meta.className = 'hint';
        meta.textContent = `${new Date(record.created).toLocaleString()} · 请求 ${record.requestId}${record.gid ? ' · GID ' + record.gid : ''}`;
        div.append(title, detail, meta);
        if (record.stage !== 'done') {
            const actions = [['query', '查询 / 重试收尾'], ['dismiss', '已自行处理']];
            if (record.downloadId !== undefined) actions.splice(1, 0, ['resume', '继续浏览器']);
            for (const [decision, label] of actions) {
                const button = document.createElement('button'); button.textContent = label;
                button.addEventListener('click', () => run(button, async () => {
                    if (decision === 'resume' && !confirm('AriaUI 可能已经创建任务。确认已检查并处理重复任务，然后继续浏览器下载？')) return;
                    if (decision === 'dismiss' && !confirm('确认已在浏览器与 AriaUI 中自行处理？此操作只关闭交接记录，不取消或恢复任何下载。')) return;
                    await rpc({ action: 'resolve', requestId: record.requestId, decision }); await refresh();
                }));
                div.append(button);
            }
        }
        $('records').append(div);
    }
}
$('settings').addEventListener('submit', event => {
    event.preventDefault();
    const btn = event.submitter || event.target.querySelector('button[type=submit]');
    void run(btn, async () => {
        const value = { enabled: $('enabled').checked, captureUnknownSize: $('captureUnknownSize').checked, minMiB: Number($('minMiB').value) };
        for (const key of ruleKeys) value[key] = $(key).value.split(/[,\n，]/).map(s => s.trim()).filter(Boolean);
        await rpc({ action: 'settings', value }); $('feedback').textContent = '设置已保存。';
    });
});
$('diagnose').addEventListener('click', event => run(event.target, async () => {
    $('connection').textContent = '正在连接…';
    try { const response = await rpc({ action: 'diagnose' }); $('connection').textContent = `已连接 · ${response.instanceId}`; }
    catch (error) { $('connection').textContent = '连接失败，请检查宿主注册和应用路径。'; throw error; }
}));
$('send').addEventListener('submit', event => {
    event.preventDefault();
    const btn = event.submitter || event.target.querySelector('button[type=submit]');
    void run(btn, async () => {
        const response = await rpc({ action: 'send', url: $('url').value.trim() });
        $('url').value = '';
        $('feedback').textContent = reasons[response.record.reason] || '请查看交接记录，确认结果前不要重复发送。';
        await refresh();
    });
});
$('refresh').addEventListener('click', event => run(event.target, () => refresh()));
void run($('refresh'), () => refresh(true));
