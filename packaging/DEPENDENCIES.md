# AriaUI 软件依赖清单 (Software Dependencies)

本文件列出 AriaUI 及其原生组件 (`libaria2.so` + `libaria2_bridge.so`) 的构建期和运行期依赖环境要求。

---

## 1. 目标平台支持 (Target Platforms)

- **操作系统**: Linux x86_64 (glibc >= 2.31, 支持 Ubuntu 20.04+, Debian 11+, Fedora 34+, Arch Linux 等主流发行版)
- **架构**: x86_64 / amd64
- **控制端口要求**: **0 TCP/HTTP/WebSocket 端口**。AriaUI 原生架构通过进程内直接 C ABI 调用驱动引擎，完全无需监听任何 TCP 端口（如 6800）。外部浏览器通信仅使用本地标准 Unix Domain Socket (`0700/0600` 属主权限隔离)。

---

## 2. 运行期依赖 (Runtime Dependencies)

### 2.1 基础 C/C++ 运行时
- **glibc**: >= 2.31
- **libstdc++**: >= 6.0.28 (GCC 10+)
- **libpthread**: 包含在 glibc 中

### 2.2 原生 aria2 / Bridge 共享库依赖
`libaria2.so` 与 `libaria2_bridge.so` 已预编译并包含在发布的 `runtimes/linux-x64/native/` 目录下：
- **libcrypto / libssl** (OpenSSL 1.1 或 3.x): 用于 HTTPS / TLS 加密与校验和计算
- **zlib** (libz.so.1): 用于 HTTP gzip 压缩传输解压
- **libxml2** (libxml2.so.2): 用于 Metalink XML 解析
- **libdl**: 动态库加载支持

### 2.3 桌面 GUI 运行时 (Avalonia UI)
- **X11 / Wayland**: 图形显示服务支持
- **libX11.so.6**, **libXrandr.so.2**, **libXi.so.6** (X11 环境)
- **libfontconfig.so.1** / **libfreetype.so.6**: 字体渲染
- **.NET Runtime**: 
  - **Framework-dependent 部署**: 需系统已安装 `.NET 10.0 Runtime` 或 `.NET 10.0 Desktop Runtime`
  - **Self-contained / Native AOT 部署**: **无需预装任何 .NET 环境**，运行时已完整静态链接/打包进二进制中。

### 2.4 aria2c CLI 预装要求
- **无需预装 aria2c**：AriaUI 完全内嵌 `libaria2.so`，系统 `PATH` 中不需要 `aria2` 或 `aria2c` 可执行文件。

---

## 3. 构建期依赖 (Build Dependencies)

用于从源码完全重新编译整个项目（含 C++ 原生桥接库与 Native AOT 程序）：

| 工具 / 库 | 最低版本要求 | 用途 |
|---|---|---|
| **.NET SDK** | 10.0.100+ | 编译 C# 应用程序、Avalonia XAML 及 Native AOT 发布 |
| **GCC / G++** | 10.0+ (支持 C++14) | 编译 `libaria2_bridge.so` 及 upstream aria2 |
| **GNU Make** | 4.0+ | 构建 libaria2 自动化工程 |
| **Autoconf / Automake** | 2.69+ / 1.15+ | 生成 aria2 构建系统配置 (若从 git clone 重新 autoreconf) |
| **Libtool** | 2.4.6+ | 共享库构建辅助 |
| **OpenSSL 开发包** | `libssl-dev` (Debian/Ubuntu) 或 `openssl-devel` (RHEL/Fedora) | libaria2 TLS 加密编译 |
| **zlib 开发包** | `zlib1g-dev` 或 `zlib-devel` | libaria2 压缩库头文件 |
| **libxml2 开发包** | `libxml2-dev` 或 `libxml2-devel` | libaria2 Metalink 解析头文件 |
| **clang / zlib** | 可选 | 交叉编译或 ASan/UBSan 内存诊断构建 |
