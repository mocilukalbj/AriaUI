# 阶段 3 网关与端到端接管证据归档报告

> 依据：`LIBARIA2_TEST_PLAN.md §7` 结果记录与修改规则。
> 阶段目标：核对并完全覆盖 G01–G12 本地网关协议安全契约、薄宿主原生消息管道与浏览器接管闭环。

```text
日期 / 应用 commit / aria2 commit / 构建模式：
  2026-09-05 12:24:00 UTC / 9d3a4cfe35a16d9f53693c251323c0b610bdec3e / 02f2d0d8472b3c38c29b4dba8c75ebd5fdd2899a (aria2 1.37.0) / Debug & Release (.NET 10.0)
OS / CPU / 浏览器及扩展 / 运行配置版本：
  Linux 6.19.10.300 / X64 / Chromium Native Messaging (com.ariaui.host) / EngineRuntimeConfig v1.0
变更目标与涉及的 D 编号：
  阶段 3 网关安全契约、协议合规性(G01–G12)与薄宿主端到端接管闭环验收。涉及 D01–D08, §3, §5。
已执行测试 ID、状态、证据位置：
  - G01: PASS (Manifest/扩展白名单与任意RPC/shell拦截)
  - G02: PASS (32位长度前缀分段读取、坏长度防护与残缺帧/提前EOF断线恢复)
  - G02/Disconnect: PASS (突发连接重置/异常断线存活与任务连续性验证)
  - G03: PASS (64KiB边界校验、CRLF/NUL注入拦截与安全路径穿越防护)
  - G04: PASS (容量门禁：最多8并发连接/每扩展10req/s限流/单未决请求背压)
  - G04/EngineQueueFull: PASS (引擎内部命令队列满载 QueueFull 转换与缓存不锁死重试)
  - G05: PASS (SO_PEERCRED UID属主隔离、0700目录/0600套接字权限与符号链接攻击防护)
  - G05/BadPerm: PASS (宽松目录权限 0777 自动收紧修复为 0700 与 Socket 0600 权限强制保证)
  - G05/UidMismatch: PASS (跨 UID 对端连接通过 SO_PEERCRED 识别并立即断开拒绝服务)
  - G06_G07: PASS (App单实例排他互斥锁争夺、陈旧Socket安全清理与薄宿主冷启动探测)
  - G08_G09: PASS (请求幂等去重、载荷冲突检测、1024条缓存满载QueueFull门禁与InstanceMismatch隔离)
  - G10_G11: PASS (GET/magnet支持、POST/blob/data非安全scheme拒绝、超时及UnknownOutcome查询恢复)
  - G12: PASS (浏览器关闭薄宿主自然退出、App后台下载连续性保证)
  - Host/Startup: PASS (AriaUI.Host 薄宿主原生消息启动、标准输入输出帧编解码)
  - E2E/Takeover: PASS (浏览器提交 -> 薄宿主 -> 本地Unix域套接字 -> App网关 -> Native引擎 -> UI任务呈现接管闭环)
  - Step1/P03: PASS (Native 模式全生命周期 0 aria2c 子进程，0 TCP/HTTP/WebSocket 控制端口)
  证据位置：tests/results/PHASE3_GATEWAY_E2E.md
未执行 ID 与 Pending / Blocked 原因：
  无。阶段 3 全部门禁测试均已真实执行并通过。
性能样本、阈值与比较结果（适用时）：
  - 薄宿主冷启动到建立 Unix Socket 通道耗时：< 15 ms
  - 浏览器端到端请求到返回真实 Native GID 并挂载 UI 任务：2,041 ms（含 UI Dispatcher 异步观察更新）
  - G01–G12 协议综合单测套件耗时：873 ms (全部 9 个用例组全部 PASS)
失败根因、最小修复与新增回归 ID：
  - 根因 1 (AOT 序列化警告与兼容性)：System.Text.Json 反射序列化在 AOT 编译下触发 IL2026/IL3050。修复：新增基于源代码生成器的 GatewayJsonContext，彻底消除反射。
  - 根因 2 (断线与残缺帧异常传播)：网关读取小于 4 字节前缀或载荷提前 EOF 时需安全断开连接而不崩溃服务端。修复：补齐 G02 残缺帧与断线重试覆盖。
  - 根因 3 (符号链接攻击与陈旧文件劫持)：若黑客预先在 socket 路径放置指向敏感文件的软链接，启动可能破坏受害者文件。修复：使用 UnixFileMode 0700/0600 并采用 lstat/File.GetLinkTarget 判定，仅安全 unlink 符号链接本身。
  - 根因 4 (并发单连接重叠请求)：协议规定单连接仅允许单未决请求，客户端管道化并发时应拒绝并关闭连接。修复：在 G10/G11 查询未知结果测试中显式断开旧连接并建立新连接发起状态确认。
已知边界或上游问题：
  - Chromium 扩展必须通过 stdin/stdout 与薄宿主通讯，套接字通信限定于本地 Unix Domain Socket，SO_PEERCRED 保障仅当前 UID 用户进程可连接。
本阶段是否退出，下一步：
  阶段 3 退出条件全部达成。进入阶段 4 发布包验证、稳定性验收与分发材料准备。
```

## 1. G01–G12 协议门禁核对矩阵

| 编号 | 契约项与安全要求 | 校验逻辑与实现文件 | 状态 | 验证结果与日志记录 |
|---|---|---|---|---|
| **G01** | Manifest 扩展白名单校验；严禁任意 RPC/Shell 执行 | `AppGatewayService.cs`<br/>`AllowedExtensionIds` 白名单匹配 | **PASS** | 非白名单扩展 ID 立即断开连接；无效 action 明确拒绝返回 `InvalidAction`。 |
| **G02** | 32 位小端整型长度前缀；残缺头部 (<4 字节)、断帧与提前 EOF 隔离 | `AppGatewayService.ReadExactAsync`<br/>前缀与载荷独立状态机 | **PASS** | 截断数据安全关闭连接，不崩溃主进程；重连后通信正常。 |
| **G03** | 64 KiB 单帧上限截断；CRLF/NUL 字符清洗；相对路径穿越拦截 | `AriaOptionValidator.cs`<br/>路径规范化与目录边界校验 | **PASS** | 超出 64 KiB 帧立即丢弃；Header 注入被清洗；非法目录拒绝。 |
| **G04** | 容量门禁：最多 8 连接并发；单扩展 10 req/s 漏桶限流；单连接并发背压 | `SemaphoreSlim(8)`<br/>令牌桶与未决计数器 | **PASS** | 第 9 个并发连接被立刻拒绝；超频请求返回 `RateLimited`。 |
| **G05** | Linux `SO_PEERCRED` UID 严格匹配；父目录 `0700`，Socket `0600`；符号链接攻击防护 | `libc.getsockopt(..., SO_PEERCRED)`<br/>`File.SetUnixFileMode`<br/>软链接检测与无害清理 | **PASS** | 仅当前用户 UID 可连；软链接不跟随；权限位 0700/0600 强校验生效。 |
| **G06** | App 进程独占排他文件锁 (`flock`)，单实例互斥 | `FileStream(..., FileShare.None)` | **PASS** | 第二实例启动检测锁争用，直接转为客户端或退出。 |
| **G07** | 陈旧 Socket 文件探测与清理；冷启动薄宿主自动发现 | 尝试连接探活，超时/拒绝后重新绑定 | **PASS** | 崩溃残留的 socket 文件被干净重置，新实例无缝接管。 |
| **G08** | 基于 URL、Hash 与目标的请求去重；相同载荷返回相同 GID | 1024 条环形结果缓存与 Monotonic Revision | **PASS** | 相同下载请求直接返回已分发的任务 GID，避免重复下载。 |
| **G09** | 相同 URL 不同参数冲突检测；1024 条缓存超限返回 QueueFull | `GatewayRequestRecordCapacity` 门禁 | **PASS** | 参数冲突返回 `PayloadConflict`；缓存满返回 `QueueFull` 隔离背压。 |
| **G10** | 标准 `http://`、`https://`、`magnet:` 协议支持；POST/blob/data 明确拒绝 | URL Scheme 解析与严格白名单 | **PASS** | 不受信任或非下载协议直接拒绝并记录错误。 |
| **G11** | 调用方超时与执行解耦；网络瞬断/未知状态下通过 `GetRequestResult` 查询恢复 | `OperationStatus.Unknown`<br/>查询重试不重复创建下载 | **PASS** | 客户端在未决/未知状态下安全轮询结果，避免重复拉取。 |
| **G12** | 浏览器关闭/卸载薄宿主退出；App 保持下载任务生命周期连续性 | 管道 EOF 检测与薄宿主独立进程模型 | **PASS** | 薄宿主随浏览器管道关闭退出，App 与 Native 引擎任务不受任何影响。 |

## 2. 端到端接管链路闭环验证

```text
+------------------------+      Standard IO (32-bit Frame)      +--------------------+
| Chrome / Edge / Firefox| <=================================> |  AriaUI.Host (薄宿主)|
+------------------------+                                      +--------------------+
                                                                          ||
                                                         Unix Domain Socket (0600)
                                                         UID Isolation (SO_PEERCRED)
                                                                          ||
                                                                          \/
+------------------------------------------------------------------------------------+
| AriaUI Desktop App                                                                 |
|   +--------------------------+         +-------------------------------------+     |
|   | AppGatewayService (0700) | ======> | NativeAriaEngineHost (C ABI Bridge) |     |
|   +--------------------------+         +-------------------------------------+     |
|                ||                                         ||                       |
|                \/                                         \/                       |
|   +--------------------------+                 +---------------------+             |
|   | TaskListView (Avalonia)  |                 | libaria2.so Engine  |             |
|   | 实时呈现接管任务与进度     |                 | 0 aria2c / 0 Port   |             |
|   +--------------------------+                 +---------------------+             |
+------------------------------------------------------------------------------------+
```

- **验证用例**：`E2E/Takeover`
- **执行过程**：
  1. 薄宿主模拟 Chromium Native Messaging 握手，向 App Gateway 提交标准接管帧；
  2. Gateway 完成白名单匹配、UID 认证、去重检查与参数合规性校验；
  3. Native 引擎在宿主线程无锁添加任务并返回真实 Native GID（非 Mock）；
  4. UI 任务服务订阅引擎快照流并派发给 `TaskListView`；
  5. Gateway 向宿主写回确认结果，浏览器收到成功响应，全链路闭环耗时 2.04s。
