# Windows x64

本分支为 Windows 10/11 x64 提供内嵌 libaria2 下载引擎。主程序不启动 aria2c，也不使用 TCP RPC。Linux 继续使用原有 `.so` 和 Unix Socket 路径。

## 构建

需要 .NET 10 SDK、PowerShell 和 MSYS2 UCRT64 工具链。在 MSYS2 的 UCRT64 终端安装：

```sh
pacman -S --needed mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-aria2
```

在仓库根目录的 PowerShell 中：

```powershell
# 只构建原生库（默认工具链目录可通过参数修改）
.\packaging\build_native_windows.ps1 -MsysPrefix C:\msys64\ucrt64
dotnet build -c Release -r win-x64

# 发布主程序、宿主和全部运行依赖；目标电脑不需要安装 .NET 或 MSYS2
.\packaging\build_release_windows.ps1 -MsysPrefix C:\msys64\ucrt64
```

发布目录为 `publish\win-x64`，启动 `AriaUI.exe`。整个目录需要一起复制，不能只复制 EXE。OpenSSL 的 `ossl-modules\legacy.dll` 是 aria2 初始化所必需的运行组件，发布脚本会一并打包。

使用 `-NativeAot` 可请求 Native AOT 发布，需要额外安装 Visual Studio C++ 工具链。普通自包含发布不依赖 MSVC。

## 数据和通信

- 设置：保留现有 `%APPDATA%\AriaUI\config.json` 兼容行为。
- 下载会话和 Windows CA 证书文件：`%LOCALAPPDATA%\AriaUI`。
- Windows 使用带 `CurrentUserOnly` 限制的命名管道；管道名根据用户 SID 和单实例锁路径生成，避免不同用户或独立数据目录冲突。
- Linux 保留 Unix Socket 和 UID 检查。
- `ARIAUI_DATA_DIR` 可指定独立设置与会话数据目录（用于便携运行或隔离测试），`ARIAUI_LOCK_PATH` 可指定单实例锁；应用和宿主需要相同配置。
- `ARIAUI_NATIVE_DIR` 可显式指定匹配架构的原生库目录，主要用于开发测试。
- Windows libaria2 使用 OpenSSL，并由应用导出 Windows 根证书供 HTTPS 校验使用；无需关闭证书校验。

## 浏览器宿主

在浏览器中加载 `undone_plugin`，从扩展页面取得实际 ID。发布目录中执行：

```powershell
.\register_host_windows.ps1 -ExtensionId 实际扩展ID -Browser Chrome
# 或同时配置多个浏览器
.\register_host_windows.ps1 -ExtensionId 实际扩展ID -Browser Chrome,Edge
# 只删除所选浏览器的当前用户注册项
.\register_host_windows.ps1 -Action Uninstall -Browser Chrome
```

脚本只写 HKCU，不需要管理员权限。不同浏览器若使用不同扩展 ID，应分别安装并使用各自的注册清单。移动发布目录后需重新注册宿主路径。

宿主优先寻找同目录 `AriaUI.exe`；也可以设置 `ARIAUI_APP_PATH` 指向正式 EXE。应用未启动时宿主会尝试启动并连接。注册脚本不会自动开启扩展接管开关。

## 验证

```powershell
$env:ARIAUI_NATIVE_DIR = (Resolve-Path .\runtimes\win-x64\native).Path
dotnet run --project tests\AriaUI.Tests -c Release -r win-x64
# 增加真实 HTTPS 下载（需要网络）
dotnet run --project tests\AriaUI.Tests -c Release -r win-x64 -- --https
```

Windows 测试入口使用真实引擎、临时目录和本地 HTTP 测试服务器，检查 DLL 加载、中文路径下载内容、命名管道、去重和结果查询、实例隔离、单实例锁、退出保存。设置 `ARIAUI_TEST_HOST` 为构建后的 `AriaUI.Host.exe` 绝对路径可额外验证真实宿主 stdio 桥接；`ARIAUI_TEST_DIR` 可指定测试文件目录。测试不修改真实浏览器注册项。

构建基础：[MSYS2 aria2 包](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-aria2)、[Native AOT 前置要求](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。原生依赖版本应在发布时记录并保留对应许可证和源码来源。
