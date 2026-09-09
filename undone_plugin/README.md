# AriaUI Companion 浏览器扩展

AriaUI Companion 是配合 [AriaUI](https://github.com/mocilukalbj/AriaUI) 使用的 Manifest V3 浏览器扩展。它将浏览器下载交给本机 AriaUI，下载进度、保存目录与任务管理由主程序负责。

**本扩展参考并基于 [alexhua/Aria2-Explorer](https://github.com/alexhua/Aria2-Explorer) 的代码裁剪、改造。感谢 Alex Hua 及原项目贡献者。** 原项目采用 BSD-3-Clause 许可证，本目录保留其版权声明和完整 [LICENSE](LICENSE)。本扩展是面向 AriaUI 的独立改造版本。

## 功能

- 自动接管符合规则的 HTTP / HTTPS GET 下载（默认允许未知大小，匹配窗口 10 秒，等价并发请求自动接管）。
- 右键链接提供两个清晰入口，点击后直接提交：
  - **用 AriaUI 下载（携带 Cookie / Referer）**：面向需要登录或防盗链的直链，显式提取目标下载 URL 匹配的 Cookie、当前页面 Referer 和 User-Agent，绕过自动请求关联。
  - **仅发送链接到 AriaUI**：保持普通 GET 和 magnet 发送，不读取 Cookie。
- 在设置页手动发送 HTTP、HTTPS 或 magnet 链接。
- 按域名、文件扩展名和大小筛选下载；`Alt+A` 切换自动接管。
- 显示交接记录及“最近一次接管检查”，详细区分未接管原因（规则过滤、等价/歧义候选、非 GET、Blob 等）。
- 使用与 AriaUI 主程序一致的图标。

扩展通过 `com.ariaui.downloader` Native Messaging 宿主连接主程序：Windows 使用命名管道，Linux 使用 Unix Socket。当前版本不使用原 Aria2-Explorer 的 TCP RPC 配置或内嵌 AriaNG 页面。

## 安装扩展

需要 Chromium 116 或更新版本所提供的扩展 API，以及包含 Native Messaging 宿主的 AriaUI。自编译 Chromium 可使用下述 Chromium 配置。Chrome / Edge 也提供相应注册选项，具体运行情况需在目标浏览器验证。

1. 将发布的 ZIP 解压到固定目录；选择包含 `manifest.json` 的文件夹。
2. Chromium / Chrome 打开 `chrome://extensions`；Edge 打开 `edge://extensions`。
3. 开启“开发者模式”，点击“加载已解压的扩展程序”。
4. 点击扩展图标打开设置，记下页面显示的扩展 ID。
5. 按下文注册宿主，再点击“检查连接 / 启动 AriaUI”。

Windows Chrome 对非商店 CRX 的安装有限制，开发测试建议使用 ZIP 解压加载方式，见 [Chrome 分发说明](https://developer.chrome.com/docs/extensions/how-to/distribute)。CRX3 包适用于允许该安装方式的环境，签名有效不代表浏览器一定允许安装。

更新解压版时，覆盖**实际已加载目录**中的文件，然后在扩展管理页点击“重新加载”，并重新打开设置页。覆盖另一个解压目录不会更新浏览器当前加载的版本。

## Windows 宿主配置

在包含 `AriaUI.exe`、`AriaUI.Host.exe` 和 `register_host_windows.ps1` 的发布目录打开 PowerShell：

```powershell
# 自编译 Chromium：将占位内容替换为扩展设置页显示的实际 ID
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\register_host_windows.ps1 -Browser Chromium -ExtensionId 实际扩展ID

# Google Chrome 或 Microsoft Edge 分别使用：
# -Browser Chrome
# -Browser Edge
```

脚本只注册当前用户，不需要管理员权限。它写入对应浏览器的 `NativeMessagingHosts\com.ariaui.downloader` 注册项，并生成宿主清单，清单中的 `path` 指向 `AriaUI.Host.exe`，`allowed_origins` 必须与实际扩展 ID 匹配。

本地已导出签名包及其配套解压包的 ID 为 `ehicmejmhlcflckpndnpmedmfkopcdfb`。直接加载仓库源码或换用其他签名密钥时，ID 可能不同，请以扩展页面为准。自定义 Chromium 若修改了上游宿主查找逻辑，也需按其实际实现调整注册位置。

移动 AriaUI 发布目录后需重新运行注册脚本。应用与宿主默认从同一发布目录启动；开发时可通过 `ARIAUI_APP_PATH` 指定应用路径。使用 `ARIAUI_DATA_DIR` 等自定义环境变量时，宿主和应用必须使用一致的配置。

卸载宿主注册：

```powershell
.\register_host_windows.ps1 -Action Uninstall -Browser Chromium
```

## Linux 宿主配置

将发布好的 `AriaUI.Host` 设置为可执行文件，并创建 `com.ariaui.downloader.json`。Chromium 常用目录为 `~/.config/chromium/NativeMessagingHosts/`，Google Chrome 为 `~/.config/google-chrome/NativeMessagingHosts/`。自定义用户目录或发行版封装可能改变位置。

```json
{
  "name": "com.ariaui.downloader",
  "description": "AriaUI Native Messaging Host",
  "path": "/absolute/path/to/AriaUI.Host",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://YOUR_EXTENSION_ID/"]
}
```

替换绝对路径和扩展 ID。详细测试步骤见 [LINUX_TESTING.md](LINUX_TESTING.md)。Linux 实机、Snap / Flatpak 环境尚未完成验证。

## 开启自动接管

**首次安装默认关闭自动接管；连接成功不会自动打开开关。**

1. 勾选“启用自动接管”，点击“保存设置”。
2. 新安装默认允许未知大小，最小大小为 `0`，域名与扩展名列表为空。已有配置升级时保留原有设置。
3. 重新发起一次网页下载。接管只针对新下载事件，不会接管已经完成的任务。

规则按实际下载地址匹配，排除优先；非空允许域名列表与允许扩展名列表需要同时满足。域名支持 `*`；`*.example.com` 不匹配裸域 `example.com`，需要分别填写。扩展名可填 `zip, iso`。未知大小开关开启后，未知大小的下载不受最小大小限制。

## 接管边界与异常处理

自动接管需要匹配到有效的网络请求：
- **匹配时间窗口**：常规匹配窗口为 10 秒；若仅存在唯一候选或所有候选完全等价，允许适度延长至 15 秒以支持慢响应下载。
- **并发请求接管**：同地址的多个候选请求均为已观察到的可重放 GET、且最终 URL、来源及关键请求头一致时，视为等价并支持每次认领一个候选；若参数有差异、存在无法排除的 POST 或尚未取得响应的候选，则不会随意挑选。
- **请求类型放宽**：不再仅因 `xmlhttprequest`、`media` 等请求类型拒绝，结合实际下载事件、GET 方法、最终 URL、来源和响应综合判断。
- **排除边界**：POST（含 POST 后跳转）、认证头（Authorization）、范围请求（Range）、blob / data 链接、无痕下载、其他扩展创建的下载、浏览器尚未标记为安全的下载会被跳过。
- **交接流程**：交接时先暂停浏览器下载，再提交给 AriaUI；收到有效任务 GID 后才取消浏览器原下载。每次提交前验证连接，空闲连接失效时只重试握手。结果不明时保留记录并查询原请求，不自动重复提交；同一未决记录只自动弹窗提醒一次，未决状态继续在记录和角标中显示。此时请检查 AriaUI 和浏览器下载列表，再决定继续浏览器或关闭记录。“已交给 AriaUI”仅表示任务创建成功，后续下载错误请在主程序中查看。

## 右键兜底与能力边界

- **用 AriaUI 下载（携带 Cookie / Referer）**：
  1. 仅根据**目标下载 URL**读取 Cookie，匹配域名、路径、Secure、有效期及当前标签页存储区；支持分区 Cookie 的浏览器环境会匹配当前分区，无法确定分区时不混用其他分区数据。
  2. Referer 来自右键所在页面或框架，去除账号信息与 fragment，采用保守跨域来源规则（同源发送完整 URL，跨域发送 origin/，HTTPS→HTTP 不发送 Referer）。
  3. 附带浏览器 User-Agent，但不采集其他网站整份 Cookie，也不将 Cookie 写入日志或交接记录。
  4. **跨域重定向凭据风险与尽力探测**：底层 aria2/libaria2 会在跟随重定向时继续向后续地址发送传递的自定义 Cookie 请求头。扩展在提交前会对携带 Cookie 的链接进行预先探测（HEAD 及 GET 兜底校验最终地址 origin）；若探测到跨域跳转，将主动拦截并给出明确提示。但需注意其能力边界：
     - **预先探测无法约束实际下载跳转**：预先探测仅检查探测请求的最终重定向地址，无法捕获中间跳转（例如 A → B → A 重定向中途已向第三方域 B 暴露），且 HEAD 与实际 GET、预先探测与 aria2 后续下载可能得到不同的跳转响应。彻底杜绝凭据泄露必须在实际下载过程中执行（例如由下载引擎按每一跳作用域发送 Cookie，或禁止携带原始 Cookie 的任务跟随重定向），因此预先探测不能称为“杜绝泄露”。
     - **HTTP→HTTPS 属于跨源拦截**：按照浏览器同源策略标准，协议不同即为跨源（origin 改变），当前 origin 比较会拦截此类重定向，并不会“同源放行”。如确认为公开资源或无需 Cookie，请使用“仅发送链接到 AriaUI”。
     - **探测使用浏览器上下文**：探测通过 `credentials: 'include'` 让浏览器自行选择 Cookie，不手动设置禁止脚本设置的 `Cookie` 请求头。探测所用 Cookie 不保证与右键框架提取并发送给 AriaUI 的 Cookie 相同，尤其是分区 Cookie；Referer 等上下文也可能不同。因此探测成功不代表登录下载一定成功。
     - **探测失败阻止提交**：HEAD 不成功时尝试 Range GET；GET 仍返回 4xx/5xx、探测超时或最终网络请求失败时，不提交携带 Cookie 的任务。浏览器上下文缺少登录状态也可能造成探测失败。
- **仅发送链接到 AriaUI**：普通 GET 发送，不读取 Cookie，不受跨域重定向凭据限制影响，适用于公开直链及 magnet 链接。

## 排错

| 现象 | 检查方法 |
| --- | --- |
| 连接失败 | 检查浏览器类型、宿主 EXE 路径、实际扩展 ID；Chromium 使用 `-Browser Chromium`。 |
| 已连接，仍由浏览器下载 | 确认自动接管已保存；发起新下载后，在设置页点击“刷新”，查看“最近一次接管检查”。 |
| 大小未知而被跳过 | 勾选“允许接管大小未知的下载”并保存（新安装已默认勾选）。 |
| 未观察到对应网络请求 | 检查浏览器扩展详情中的网站访问权限，确认允许访问实际下载站点。 |
| 更新后界面没有变化 | 检查覆盖的是否为已加载目录，重新加载扩展并重新打开设置页。 |
| 交接结果待确认 | 查看原请求记录和主程序任务，避免反复发送造成重复下载。 |

## 数据与权限

`downloads` 用于下载事件和暂停/取消/恢复；`nativeMessaging` 用于宿主通信；`webRequest` 与网站权限用于匹配请求；`storage` 保存设置和交接状态；`contextMenus`、`notifications`、`alarms` 用于右键入口、异常提示和恢复检查；`cookies` 权限用于右键明确选择“携带 Cookie / Referer”时读取目标链接所需的 Cookie。

自动接管按需转发观测到的 Cookie、Referer、User-Agent 等请求头给本机 AriaUI。请求头只在扩展内存中短暂保留，不写入交接记录。交接记录包含文件名标签、请求 ID、任务 GID 和状态等，不保存完整 URL 或 Cookie。最近一次接管检查只记录时间和原因。

## 开发与打包

在仓库根目录使用 PowerShell 7：

```powershell
.\packaging\build_extension.ps1 -OutputDirectory .\out\extension -KeyPath .\out\signing\ariaui-companion.pem
```

脚本生成 CRX3、ZIP 和 `extension-id.txt`，并将公钥写入发布包清单，使 CRX 和配套解压包具有相同 ID。后续版本复用同一私钥；私钥需自行保管，不得放入分发包或提交到公开仓库。

当前 Windows 自编译 Chromium 解压加载版已由用户确认可以连接并接管下载；这不代表所有站点、浏览器或 Linux 环境均已验证。

## 代码来源与致谢

- [Aria2-Explorer — alexhua](https://github.com/alexhua/Aria2-Explorer)：本扩展参考并基于其代码裁剪改造，涉及浏览器下载接管、右键入口及规则设置等原有实现。原项目采用 [BSD-3-Clause](https://github.com/alexhua/Aria2-Explorer/blob/master/LICENSE)，保留 `Copyright (c) 2023, Alex Hua` 和本目录完整许可证。
- [AriaNg — mayswind](https://github.com/mayswind/AriaNg)：原项目使用的管理前端，采用 MIT 许可证。当前运行包不包含内嵌 AriaNG 管理页面；仓库中保留的历史资源及其许可证用于来源追溯。
- Native Messaging、交接状态处理、简化设置页和 AriaUI 平台适配是本次改造的主要方向。来源与历史说明另见 [acknowledgment.txt](acknowledgment.txt)。

再分发时请随源码或发布包保留适用的版权声明、许可证和免责声明。原始 TXT 讨论仅作为历史资料，其中旧路径与旧状态不是当前安装说明。
