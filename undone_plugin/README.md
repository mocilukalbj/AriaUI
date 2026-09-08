# AriaUI Companion 浏览器扩展

AriaUI Companion 是配合 [AriaUI](https://github.com/mocilukalbj/AriaUI) 使用的 Manifest V3 浏览器扩展。它将浏览器下载交给本机 AriaUI，下载进度、保存目录与任务管理由主程序负责。

**本扩展参考并基于 [alexhua/Aria2-Explorer](https://github.com/alexhua/Aria2-Explorer) 的代码裁剪、改造。感谢 Alex Hua 及原项目贡献者。** 原项目采用 BSD-3-Clause 许可证，本目录保留其版权声明和完整 [LICENSE](LICENSE)。本扩展是面向 AriaUI 的独立改造版本。

## 功能

- 自动接管符合规则的 HTTP / HTTPS GET 下载。
- 右键链接选择“直接用 AriaUI 下载链接”，点击后直接发送；成功时不弹通知，未确认时提示查看记录。
- 在设置页手动发送 HTTP、HTTPS 或 magnet 链接。
- 按域名、文件扩展名和大小筛选下载；`Alt+A` 切换自动接管。
- 显示交接记录及“最近一次接管检查”，帮助定位未接管的原因。
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
2. 初次测试可保持允许/排除列表为空、最小大小为 `0`。
3. 如希望接管未提供文件大小的下载，勾选“允许接管大小未知的下载”，再保存。
4. 重新发起一次网页下载。接管只针对新下载事件，不会接管已经完成的任务。

规则按实际下载地址匹配，排除优先；非空允许域名列表与允许扩展名列表需要同时满足。域名支持 `*`；`*.example.com` 不匹配裸域 `example.com`，需要分别填写。扩展名可填 `zip, iso`。未知大小开关开启后，未知大小的下载不受最小大小限制。

## 接管边界与异常处理

自动接管需要匹配到唯一、近期的 GET 网络请求。POST（含 POST 后跳转）、认证头、范围请求、blob / data 链接、无痕下载、其他扩展创建的下载、浏览器尚未标记为安全的下载会被跳过。同一地址并发请求、缺少网站访问权限、长时间停留在保存文件对话框等，也可能使请求无法匹配。

交接时先暂停浏览器下载，再提交给 AriaUI；收到有效任务 GID 后才取消浏览器原下载。结果不明时保留记录并查询原请求，不自动重复提交。此时请检查 AriaUI 和浏览器下载列表，再决定继续浏览器或关闭记录。“已交给 AriaUI”仅表示任务创建成功，后续下载错误请在主程序中查看。

右键和手动发送创建 GET 请求，不携带浏览器 Cookie，需要登录的下载应优先从网页发起并使用自动接管。手动重复点击同一链接会创建新的任务。

## 排错

| 现象 | 检查方法 |
| --- | --- |
| 连接失败 | 检查浏览器类型、宿主 EXE 路径、实际扩展 ID；Chromium 使用 `-Browser Chromium`。 |
| 已连接，仍由浏览器下载 | 确认自动接管已保存；发起新下载后，在设置页点击“刷新”，查看“最近一次接管检查”。 |
| 大小未知而被跳过 | 勾选“允许接管大小未知的下载”并保存。 |
| 未观察到对应网络请求 | 检查浏览器扩展详情中的网站访问权限，确认允许访问实际下载站点。 |
| 更新后界面没有变化 | 检查覆盖的是否为已加载目录，重新加载扩展并重新打开设置页。 |
| 交接结果待确认 | 查看原请求记录和主程序任务，避免反复发送造成重复下载。 |

## 数据与权限

`downloads` 用于下载事件和暂停/取消/恢复；`nativeMessaging` 用于宿主通信；`webRequest` 与网站权限用于匹配请求；`storage` 保存设置和交接状态；`contextMenus`、`notifications`、`alarms` 用于右键入口、异常提示和恢复检查。

自动接管按需转发观测到的 Cookie、Referer、User-Agent 等请求头给本机 AriaUI。请求头只在扩展内存中短暂保留，不写入交接记录；扩展不主动读取浏览器 Cookie 仓库。交接记录包含文件名标签、请求 ID、任务 GID 和状态等，不保存完整 URL 或 Cookie。最近一次接管检查只记录时间和原因。

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
