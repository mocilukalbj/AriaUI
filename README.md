# AriaUI - 跨平台 Aria2 下载管理器

<p align="center">
  <img src="Assets/avalonia-logo.ico" alt="AriaUI Logo" width="96" height="96" />
</p>

<p align="center">
  <b>基于 .NET 10 + Avalonia 12.1 + Semi.Avalonia 构建的高性能跨平台 Aria2 GUI 客户端</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Avalonia-12.1.1-7A39FB?logo=avalonia" alt="Avalonia UI" />
  <img src="https://img.shields.io/badge/Architecture-MVVM-blue" alt="MVVM" />
  <img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License" />
</p>

---

## 🌟 核心特性

- ⚡ **当前平台支持**：支持 Linux (Wayland / X11) 与 Windows。
- 🚀 **aria2c 守护进程管理**：可自动拉起并管理本地 `aria2c`；关闭 `AutoStartDaemon` 后可连接自行管理的本地或远程实例。
- 📡 **全双工 WebSocket JSON-RPC 通信**：毫秒级实时双向状态同步与下载事件订阅。
- 📋 **全能下载支持**：支持 HTTP/HTTPS、FTP、SFTP、Magnet 磁力链接与 `.torrent` 种子文件下载，支持批量解析与多连接分片下载。
- 🏷️ **智能状态筛选与搜索**：按下载中、等待/暂停、已完成、停止/失败实时筛选，支持文件名快速搜索与增量平滑渲染。
- 🌐 **BT Tracker 自动订阅与加速**：内置精选公共 Tracker 源，支持一键在线拉取并热注入到 Aria2 全局选项中。
- 🎨 **精美 Semi UI 设计与主题切换**：支持跟随系统、浅色、深色主题无缝切换。

---

## 🏗️ 架构概览

AriaUI 遵循严格的 MVVM 分层架构与依赖注入（IoC / DI）设计：

```
AriaUI/
├── Assets/                 # 资源文件 (图标、静态资源)
├── Converters/             # Avalonia UI 转换器
├── Helpers/                # 工具类 (格式化、安全异步扩展、任务名解析)
├── Models/                 # 数据契约与实体模型 (RPC、配置、Messenger 消息)
├── Services/               # 业务与核心服务层 (进程管理、WebSocket RPC、任务协调、文件与 Tracker 服务)
├── ViewModels/             # ViewModel 层 (CommunityToolkit.Mvvm)
├── Views/                  # XAML 视图层 (Semi.Avalonia 风格组件)
├── App.axaml (.cs)         # 应用入口与生命周期配置 (DI 容器、生命周期、主题)
├── Program.cs              # 程序引导点
├── ViewLocator.cs          # AOT 裁剪安全的视图映射器
└── app.manifest            # Windows 应用清单文件
```

### 核心技术栈

- **框架**: [.NET 10.0](https://dotnet.microsoft.com/)
- **UI 库**: [Avalonia UI 12.1](https://avaloniaui.net/)
- **主题风格**: [Semi.Avalonia 12.1](https://github.com/irihitech/Semi.Avalonia)
- **MVVM 工具包**: [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)
- **依赖注入**: `Microsoft.Extensions.DependencyInjection`

---

## 🚀 快速开始

### 运行环境准备

1. 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. 安装 `aria2` 命令行工具：
   - **Arch Linux / CachyOS**: `sudo pacman -S aria2`
   - **Ubuntu / Debian**: `sudo apt install aria2`
   - **Windows**: `scoop install aria2` 或 `winget install aria2`

### 编译与运行

```bash
# 克隆仓库
git clone https://github.com/mocilukalbj/AriaUI.git
cd AriaUI

# 还原依赖并生成
dotnet build

# 启动应用程序
dotnet run
```

### 发布独立二进制版本

```bash
# Linux x64 发布
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64

# Windows x64 发布
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

---

## ⚙️ 配置文件说明

配置持久化保存于平台标准配置目录：
- **Linux**: `$XDG_CONFIG_HOME/AriaUI/config.json`（通常为 `~/.config/AriaUI/config.json`）
- **Windows**: `%APPDATA%\AriaUI\config.json`

Windows 上若仅存在旧版 `%USERPROFILE%\.config\AriaUI\config.json`，程序会兼容读取该路径。

支持自定义配置项包括：
- `AutoStartDaemon`: 是否自动管理本地 aria2c
- `RpcHost` / `RpcPort` / `RpcUseTls` / `RpcSecret`: RPC 连接信息
- `DefaultDownloadDir`: 默认保存目录
- `MaxConcurrentDownloads`: 最大并行任务数
- `MaxConnectionPerServer`: 单服务器连接数
- `Split`: 单文件分片数
- `ThemeMode`: 主题模式 (`System` / `Light` / `Dark`)

自动管理本地 daemon 时，RPC 主机必须是 `localhost`、`127.0.0.1` 或 `::1`，端口范围为 `1024-65535`，且不启用 TLS。连接自行管理的本地或远程 aria2 时，可关闭 `AutoStartDaemon`，使用 `1-65535` 端口并按服务端配置选择 `RpcUseTls`。

托管模式仅继承用户 aria2 配置中的非托管项；daemon/RPC、目录、并发、DHT 与会话选项由 AriaUI 接管。同一配置目录仅允许一个 AriaUI 实例运行。

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源。
