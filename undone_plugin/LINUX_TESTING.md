# AriaUI Companion：Linux 实机验证

这是针对现有 AriaUI Native Messaging 协议 v1 改造的 MV3 扩展。Linux 实机验证仍待完成；当前版本另已增加 Windows 适配，通用安装与来源说明以 README.md 为准。静态检查或 Windows 测试结果不能代替 Linux 真实浏览器验收。

## 加载与连接

1. 将此目录复制到 Linux 的固定目录。Chrome / Chromium 打开 `chrome://extensions`，启用开发者模式，加载已解压的扩展，选择包含 `manifest.json` 的目录。
2. 点击工具栏的 AriaUI 图标打开设置页，复制“扩展 ID”。首次安装默认关闭自动接管，不读取旧 Aria2 Explorer 的 RPC 配置。
3. 确保现有 `AriaUI.Host` 已发布为可执行文件，宿主注册文件的 `path` 指向正式绝对路径，`allowed_origins` 精确匹配该扩展 ID。仓库原来的注册文件仍含旧开发目录与旧 ID，不能原样使用。
4. 在浏览器的 NativeMessagingHosts 用户目录注册 `com.ariaui.downloader.json`。原生 Google Chrome 通常使用 `~/.config/google-chrome/NativeMessagingHosts/`，Chromium 通常使用 `~/.config/chromium/NativeMessagingHosts/`；自行修改用户数据目录或发行版封装可能改变位置。Snap / Flatpak 不在本次范围。
5. 点击“检查连接 / 启动 AriaUI”。宿主负责启动应用并连接 Unix Socket；扩展只显示握手成功或失败，不会自动安装宿主。

注册文件示意（必须替换占位内容）：

```json
{
  "name": "com.ariaui.downloader",
  "description": "AriaUI Native Messaging Thin Host",
  "path": "/absolute/path/to/AriaUI.Host",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://YOUR_EXTENSION_ID/"]
}
```

参考：[Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)。

## 功能与边界

- 自动接管：启用后，对实际下载地址应用规则，只有唯一匹配到近期 GET 请求且能暂停浏览器下载时才提交。先暂停，再释放文件名决策回调，再提交。成功返回合法 GID 后才取消浏览器下载。
- 规则：排除优先；非空的允许域名与扩展名列表是附加限制；大小门槛始终生效。未知大小默认放行浏览器，可以单独开启。`*.example.com` 不包括裸域 `example.com`，需要分别填写。
- 所有发送入口统一走 Native Messaging，无 HTTP / WebSocket RPC 回退。旧 AriaNG 页面、远程配置与独立 aria2 启动入口已移除。
- 手动发送与右键发送支持单个 HTTP/HTTPS 链接或 magnet；HTTP 使用 GET，不携带 Cookie。magnet 用右键或粘贴发送，不注入页面拦截所有磁力链接点击。
- 自动接管只转发观测到的最终请求 Cookie、Referer、User-Agent、Origin、Accept、Accept-Language；不合并浏览器 Cookie 仓库，不读取其他分区的 Cookie。请求头只存内存，最多保留约一分钟，不写交接记录。
- POST（包括 POST 后重定向到 GET）、认证头、范围请求、非 HTTP/HTTPS、其他扩展创建的下载、无痕下载、浏览器标记为非安全的下载都跳过。
- `downloads` 不暴露对应的 `webRequest.requestId`。插件使用原始/最终 URL、可用的来源信息及成功响应时间做保守关联；响应必须在最近五秒内，且与下载开始时间相差不超过五秒。部分内容响应（206）跳过。同 URL 并发、长时间文件选择、观测缺失或后台刚恢复时可能跳过。此关联不是浏览器提供的身份保证，必须实测下载覆盖情况。
- 持有 Cookie 的 URL 被 aria2 再次请求时仍可能发生新的重定向；应用侧如何处理跨域请求头不在本次插件修改范围，实测时必须检查登录下载与重定向行为。
- 手动重复发送同一个链接视为用户新建任务；插件保证恢复过程中不自动重发原请求，不承诺 URL 全局去重。
- 交接成功不等于文件下载成功；内核后续 HTTP 错误请在 AriaUI 中查看。

## 未决记录与恢复

记录只包含版本、请求 ID、原应用实例 ID、浏览器下载 ID/开始时间、文件名标签、阶段、查询次数、GID 与状态码。完整 URL、Cookie 和请求头不持久化。

提交意图必须先写入存储，再向宿主发送。扩展恢复后，未发送的记录恢复浏览器；已发送记录只查询原请求，不创建新的 AddDownload。查询沿用原应用实例 ID，不把新实例冒充旧实例。

超时、断线、Pending、未知响应和网关 Failed 均视为结果未知。现有网关的 Failed 包含泛化异常，不能据此断言内核没有执行。只有 AddDownload 的明确接纳前拒绝才自动恢复浏览器。

后台每分钟检查未决记录，最多自动查询三次；实例变化或 UnknownOutcome 停止自动查询。仍不明确时保留暂停状态和提示，在设置页可以：

- **查询 / 重试收尾**：查询原请求，或在已有 GID 时重试浏览器取消操作。
- **继续浏览器**：在确认已检查 AriaUI 后继续原浏览器下载，不取消 AriaUI 任务。
- **已自行处理**：只结束交接记录，不操作任一下载。

已结束记录保留最近 50 条；未结束记录最多 100 条，满额时停止新交接。插件卸载/被禁用期间无法替用户恢复下载，应在浏览器下载列表中处理。浏览器取消、恢复失败或下载记录消失均提示人工处理，不删除文件或下载历史。

## 建议实测顺序

1. **连接与冷启动**：分别在应用已运行、未运行、宿主缺失、路径错误时检查连接。
2. **手动链路**：普通公开 GET 与有效 magnet，确认 AriaUI 中出现任务和真实 GID。
3. **自动成功**：开启接管，下载足够大的公开文件。确认浏览器先暂停，AriaUI 接纳后原下载取消，只有一个 AriaUI 任务。
4. **过滤与请求信息**：域名、扩展名、大小/未知大小、POST、blob、登录 Cookie、跨域重定向、同 URL 并发。无法识别的请求应留在浏览器。
5. **明确拒绝**：队列满、限流、非法文件名等，确认浏览器恢复且不误报成功。
6. **不确定结果**：提交期间结束宿主或应用、丢失返回结果。确认不自动重加，不直接恢复浏览器，并能看到待确认记录。
7. **恢复与用户操作**：重载扩展、重启浏览器；在交接期间手动取消/继续浏览器下载，确认状态提示与处理按钮。
8. **资源与权限**：确认没有旧 RPC 请求，没有 Cookie 或完整下载地址出现在日志/存储。首次启用后检查浏览器后台错误。

请记录浏览器版本、Linux 发行版、原生/沙盒安装方式、插件版本、场景、预期/实际表现及脱敏状态码。当前版本不是已经通过这些实测的发布版。
