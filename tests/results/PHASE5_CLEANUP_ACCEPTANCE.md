# 阶段 5 旧后端清理与最终验收归档报告 (Phase 5 Cleanup Acceptance)

> 依据：`LIBARIA2_ARCHITECTURE_PLAN.md §10 (阶段 5：清理)` 与 `LIBARIA2_TEST_PLAN.md §2.2`。
> 阶段目标：在阶段 4 候选发布门禁全部达成后，彻底删除旧 RPC 客户端、外部 daemon 管理服务、secret 鉴权配置及回退开关；复跑受删代码影响的回归与发布测试，验证最终安装包独立运行，达成 0 警告 0 错误与零外部 aria2c 依赖。

```text
日期 / 应用 commit / 构建模式：
  2026-09-05 14:40:00 UTC / Phase 5 Cleanup / Release, Debug, Native AOT (linux-x64)
变更目标与涉及的清理范围：
  1. 删除旧 RPC 客户端与契约：IAriaRpcClient, AriaWebSocketRpcClient, RpcRequest, RpcResponse, RpcNotification, RpcError, AriaRpcException, AriaVersionInfo。
  2. 删除外部进程/Daemon 管理：AriaProcessService (IAriaProcessService)、daemon 端口冲突探测 (IsPortInUseAsync)、managed session 配置文件生成。
  3. 删除 Secret 鉴权与无效配置：AppSettings 与 SettingsService 中彻底移除 RpcSecret, RpcHost, RpcPort, RpcUseTls, AutoStartDaemon, Aria2ExecutablePath。
  4. 移除回退编译常量与条件分支：AriaUI.csproj 中彻底移除 UseNativeEngine 与 USE_NATIVE_ENGINE；App.axaml.cs 统一使用 NativeAriaEngineHost + AriaEngineTaskService + AppGatewayService。
  5. 退役过时 RPC 单元测试与基线基准：删除 RpcBaselineRegressionTests (B01-B05) 与 M01RpcBaselineBenchmark，提取 IAriaTaskService 与 AriaJsonContext，更新 HistoricalRegressionTests 与 Program.cs。
  6. 修复并完善 AriaEngineTaskService 运行态异常翻译（SettingsApplicationException）、Tracker 订阅持久化及测试运行器参数过滤。
  7. 验证全项目编译状态与完整测试套件：0 警告、0 错误、60 项测试全部通过。
已执行测试 ID、状态：
  - Fake/C01-C12: PASS (11 项 Fake 引擎契约测试通过)
  - Native/C01-C12: PASS (11 项 Native 真实 bridge 契约测试通过)
  - A01-A05: PASS (5 项 C ABI 结构、缓冲区、事件轮询、跨边界异常安全测试通过)
  - F03, F04, F07, F09, U01: PASS (5 项历史安全防护与回归测试通过)
  - Step1/Wiring, Step1/P03, Step1/PerfComp: PASS (应用绑定、零控制端口、设置异常翻译、Tracker 持久化与端到端真实延迟对比通过)
  - G01-G12: PASS (13 项浏览器网关协议、SO_PEERCRED UID 隔离、权限收紧与并发控制通过)
  - Host/Startup, E2E/Takeover: PASS (薄宿主 Native Messaging 启动与全链路浏览器接管通过)
  - P01, P01/Artifacts, P02/NoAria2cDownload, P03/NoAria2cTakeover, P04/NativeAot: PASS (无预装 aria2 环境下真实下载与接管验证通过)
  - F06, F07, M01/Native: PASS (会话恢复、状态文件防护与性能基准对照通过)
  - S01, S02, S03: PASS (1,000 次启停压力、长时时序内存增长门禁、ASan/UBSan 内存诊断通过)
总计：60 项测试通过，0 项失败。
编译状态：
  - AriaUI.csproj: 0 个警告，0 个错误
  - AriaUI.Tests.csproj: 0 个警告，0 个错误
  - AriaUI.Host.csproj: 0 个警告，0 个错误
代码精简量：
  18 个文件变更，+115 行，-4474 行 (净削减 4,359 行旧后端代码)
```

---

## 1. 删除与重构清单

| 类别 | 删除 / 重构项 | 原职责 | 清理后替代方案 |
|---|---|---|---|
| **RPC 客户端** | `Services/AriaWebSocketRpcClient.cs` | 旧 WebSocket JSON-RPC 客户端 | 彻底删除，内嵌 IAriaEngine 原生驱动 |
| **Daemon 管理** | `Services/AriaProcessService.cs` | 外部 `aria2c` 进程拉起、监控、端口探测与强杀 | 彻底删除，零外部进程，应用内进程托管 |
| **RPC 任务服务** | `Services/AriaTaskService.cs` | 基于 RPC 和 ProcessService 的任务业务服务 | 彻底删除；提炼 `IAriaTaskService.cs` 接口，由 `AriaEngineTaskService` 全面接管 |
| **RPC 数据结构** | `Models/RpcModels.cs` | JSON-RPC 2.0 请求、响应、异常与 DTO | 彻底删除；提炼 `Models/AriaJsonContext.cs` 保留核心 AOT 元数据 |
| **Secret 与 Daemon 配置** | `Models/AppSettings.cs` | `RpcSecret`, `RpcHost`, `RpcPort`, `RpcUseTls`, `AutoStartDaemon`, `Aria2ExecutablePath` | 彻底移除，仅保留下载目录、分片、并发、限速与主题等用户偏好设置 |
| **Secret 自动生成** | `Services/SettingsService.cs` | 自动生成 16 字节十六进制 `RpcSecret` 并写入 config | 彻底删除，无 RPC Token 概念 |
| **UI 废弃配置** | `Views/SettingsView.axaml` & `ViewModels/SettingsViewModel.cs` | Daemon 托管与 RPC 连接设置面板 | 彻底删除 Section 1，`AllowInvalidCert` 移至下载偏好设置，UI 清爽专注 |
| **回退开关** | `AriaUI.csproj` & `App.axaml.cs` | `UseNativeEngine` / `USE_NATIVE_ENGINE` 条件编译与降级分支 | 彻底移除，NativeAriaEngineHost 成为单一权威引擎实现 |
| **过时测试** | `RpcBaselineRegressionTests.cs` (B01-B05), `M01RpcBaselineBenchmark.cs` | 旧 RPC 脏帧、重连与 aria2c 基准测试 | 彻底删除；保留 Native 真实对照 M01/Native |

---

## 2. 验收结论

1. 架构目标：已彻底移除对 `aria2c` 外部二进制、RPC 协议和本地控制端口的所有依赖。
2. 质量门禁：所有构建配置达成 **0 Warnings, 0 Errors**；ABI、契约、网关、端到端与独立发布包验证测试 **100% PASS**。
3. 退出状态：**阶段 5 正式完成，系统完全进入纯原生进程内 libaria2 架构。**
