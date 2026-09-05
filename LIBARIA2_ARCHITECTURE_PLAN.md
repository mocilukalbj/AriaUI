# AriaUI 内嵌 libaria2 实施指导

> 更新：2026-09-05。本文规定目标架构，未声明实现已经完成。
> 测试范围、稳定编号和执行规则见 [LIBARIA2_TEST_PLAN.md](LIBARIA2_TEST_PLAN.md)。既有行为依据见 [REVIEW.md](REVIEW.md)。

## 1. 目标与固定边界

软件以简洁、高效、故障可定位为目标：下载协议交给 aria2，业务协调留在应用服务，UI 负责交互。只保留必要的边界，不建设通用 RPC 平台、插件框架或第二套下载内核。

| 编号 | 必须遵守的决定 |
|---|---|
| D01 | 本期仅支持 Linux x64，先完成试验版本，再完成分发验收。Windows、其他架构和浏览器商店上架不属于本期门禁。 |
| D02 | UI 与内核位于同一应用进程，通过托管接口、单一 owner 线程和 C ABI 连接，不使用 aria2c 子进程。 |
| D03 | 控制链路不创建 TCP/HTTP/WebSocket 监听，不使用 RPC 端口、端口扫描、RPC secret 或失败后的端口降级。 |
| D04 | 每个应用进程同时只有一个 libaria2 session；全部 native 生命周期与调用由同一专用线程执行；Faulted 后不自动重建 session。 |
| D05 | fail-fast：立即结束失败操作并暴露真实原因；只有引擎完整性受损才使整个引擎 Faulted。不得吞错、假成功或靠自动重试掩盖故障。 |
| D06 | 命令、事件、订阅者、网关连接和请求记录都有容量边界；不以无限 Task、等待者或缓存转移背压。 |
| D07 | 浏览器接管仅使用 Native Messaging + 本用户 Unix Domain Socket；宿主是薄转发器，不能持有下载 session。 |
| D08 | 保留既有业务保护；原生链接不会自动解决 Tracker SSRF、文件删除、设置生效、重复提交和列表分页。 |

“无端口”专指控制链路。HTTP/HTTPS 下载出站连接、BitTorrent 监听、DHT UDP 等下载网络行为仍由 aria2 管理，不得为通过无控制端口测试而破坏下载功能。

REVIEW 中 #47–#60 已修复。迁移收益是减少本地控制链路和连接状态，不以“旧实现仍有上述漏洞”为前提。进程内链接失去了进程崩溃隔离，必须接受 native 致命崩溃会结束应用的取舍。

## 2. 职责与调用路径

```text
Views / ViewModels ── 应用服务（任务 / 设置 / 生命周期）
                              ▲                 │
浏览器扩展                     │                 ▼
  │ Native Messaging     IRemoteGateway      IAriaEngine
  ▼                           ▲                 │ 有界 Channel
薄宿主 ── Unix Domain Socket ──┘                 ▼
                                     NativeAriaEngineHost
                                       单一专用 owner 线程
                                                │ C ABI
                                                ▼
                                       ariaui_native_bridge
                                                │ C++ API
                                                ▼
                                            libaria2
```

- ViewModel 依赖应用服务或 `IAriaEngine` 的托管契约，不接触 P/Invoke、native handle、RPC 类型和传输细节。
- 应用服务保留添加协调、设置保存与应用、批量结果、文件删除前校验、Tracker 更新和通知。不要把现有 `AriaTaskService` 的业务职责搬进 ViewModel 或 bridge。
- host 负责线程、状态、命令接纳、请求完成和快照发布，不实现 HTTP、BT 或 Cookie 协议。
- bridge 负责转换、调用、复制快照和收集事件。保留独立 `run_once` 导出；防误调用靠统一封装和 owner 线程检查，不靠少导出一个函数。
- 原生回调只复制紧凑事件，不调托管代码、不重入 aria2。查询与进度采样在回调返回后执行。
- 网关将有限的浏览器请求转换为已有应用服务调用，不开放任意内核命令。

## 3. 阶段 0 必须完成能力映射与版本锁定

### 3.1 锁定版本与构建依赖面

依据阶段 0 要求，锁定 upstream aria2 版本与编译配置：

- **上游源码仓库**：`https://github.com/aria2/aria2.git`
- **锁定版本 Tag**：`release-1.37.0`
- **锁定 Git Commit**：`02f2d0d8472b3c38c29b4dba8c75ebd5fdd2899a`（Tag `release-1.37.0`，Annotated Tag Object: `eb7232465119e1a596cb424e2b6c3c9f4e2abb30`）
- **目标产物**：Linux x86_64 共享库 `libaria2.so` 与 C ABI 桥接层 `libaria2_bridge.so`
- **编译标准与工具链**：GNU Autotools + C++14 (GCC 11+ / Clang 14+)
- **依赖清单**：
  - `zlib` (>= 1.2.11)：用于 Metalink 和 Gzip 传输内容解压。
  - `openssl` (>= 1.1.1 / 3.0)：用于 HTTPS/WSS 传输层 TLS 加密及 SHA1/SHA256 哈希校验。
  - `libxml2` (>= 2.9.10) 或 `expat`：用于 Metalink XML 元数据解析。
  - `c-ares`（可选，推荐）：用于异步 DNS 解析，防止网络阻塞。
- **构建裁剪参数**（最小攻击面与纯净依赖）：
  ```bash
  ./configure \
      --enable-libaria2 \
      --disable-nls \
      --without-gnutls \
      --with-openssl \
      --without-libssh2 \
      --without-sqlite3 \
      --without-libuv
  ```

### 3.2 托管契约与真实 Native libaria2 API 映射表

| 能力 / 托管接口 (`IAriaEngine`) | C ABI 桥接导出 (`libaria2_bridge.so`) | 上游真实 C++ API (`namespace aria2`) | 参数转换与语义细节 | 原生缺口应用层方案 |
|---|---|---|---|---|
| **启动**<br/>`StartAsync(options)` | `a2_engine_init` | `aria2::libraryInit()`<br/>`aria2::sessionNew(KeyVals, SessionConfig)` | 启动参数转 `KeyVals`；显式配置 `keepRunning=true`、`useSignalHandler=false` 并注册 `DownloadEventCallback` | 空任务通过 `keepRunning=true` 避免直接退出；恢复任务通过 `--input-file` 传递落盘 session 文件 |
| **关闭**<br/>`ShutdownAsync()` | `a2_engine_shutdown`<br/>`a2_engine_destroy` | `aria2::shutdown(session, false)`<br/>`aria2::sessionFinal(session)`<br/>`aria2::libraryDeinit()` | 在 owner 线程推进运行循环至正常退出；逆序销毁 session 与 library | 关闭期限到达（5s）未开始命令以 `TimeoutException` 结束；严重超时调用 `aria2::shutdown(true)` |
| **循环推进** | `a2_engine_run_once` | `aria2::run(session, RUN_ONCE)` | 执行单轮原生事件循环，受 `BatchTimeBudget` (50ms) 和 `LoopTimeout` 约束 | 阶段 1 探针测量 `RUN_ONCE` 延迟，不采用高消耗忙轮询 |
| **添加 URI**<br/>`AddUriAsync(uris, options)` | `a2_download_add_uri` | `int aria2::addUri(Session*, A2Gid*, const vector<string>&, const KeyVals&, int)` | 选项接受 `IEnumerable<KeyValuePair<string, string>>` 并转为 `KeyVals`（`vector<pair<string, string>>`），**原生支持重复 header**；返回 16 位十六进制 GID | 批量逐项添加并报告独立结果，不承诺跨 URI 事务 |
| **添加 Torrent**<br/>`AddTorrentAsync(path, options)` | `a2_download_add_torrent` | `int aria2::addTorrent(Session*, A2Gid*, const string&, const KeyVals&, int)` | 校验本地文件安全路径后传入 torrent 路径与配置 `KeyVals` | 解析失败抛出明确 `EngineCommandException` |
| **暂停**<br/>`PauseAsync(gid, force)` | `a2_download_pause` | `aria2::pause(session, gid)` / `aria2::forcePause(session, gid)` | GID 十六进制解析为 `uint64_t`；`force=true` 立即中断连接 | 区分“命令已接纳”与“状态实际就绪”，状态以随后事件及快照为准 |
| **恢复**<br/>`ResumeAsync(gid)` | `a2_download_unpause` | `aria2::unpause(session, gid)` | GID 解析为 `uint64_t`，恢复排队或下载 | 任务不存在抛出 `EngineCommandException` (code 1) |
| **移除**<br/>`RemoveAsync(gid, force)` | `a2_download_remove` | `aria2::remove(session, gid)` / `aria2::forceRemove(session, gid)` | 停止任务网络和文件 IO；必须待 native 确认停止后再由应用服务删本地文件 | 删除本地文件前必须校验安全基准目录 |
| **清理历史**<br/>`PurgeDownloadResultAsync()` | `a2_download_purge_results` | `aria2::purgeDownloadResult(session)` / `aria2::removeDownloadResult(session, gid)` | 清理已完成、错误或已移除的任务结果缓存 | 遍历清理并返回受影响条目数 |
| **修改任务选项**<br/>`ChangeOptionAsync(gid, opts)` | `a2_download_change_option` | `int aria2::changeOption(Session*, A2Gid, const KeyVals&)` | 传入任务特定 `KeyVals`（支持动态限速、Header 调整等） | 实际修改结果立即可由 `GetTaskOptionAsync` 读取确认 |
| **修改全局选项**<br/>`ChangeGlobalOptionAsync(opts)` | `a2_engine_change_global_option` | `int aria2::changeGlobalOption(Session*, const KeyVals&)` | 传入全局 `KeyVals`（支持全局限速、并发度等） | 实际修改结果立即可由 `GetGlobalOptionAsync` 读取确认 |
| **读取全局选项**<br/>`GetGlobalOptionAsync()` | `a2_engine_get_global_option` | `KeyVals aria2::getGlobalOption(Session*)` | 读取当前 session 全部生效的全局选项键值对，封装为 `AriaOptionCollection`（`IReadOnlyList<KeyValuePair<string, string>>`），原生完整保留多值重复 header | 用于应用层设置同步与热更新验证，原生支持重复 header 查询 |
| **读取任务选项**<br/>`GetTaskOptionAsync(gid)` | `a2_download_get_option` | `KeyVals aria2::getOption(Session*, A2Gid)` | 读取任务实际生效选项（继承自全局与单任务覆盖），返回 `AriaOptionCollection`，支持多值重复 header 提取 | 任务不存在抛出 `EngineCommandException` (code 1) |
| **分页查询**<br/>`GetTasksPagedAsync(filter, offset, limit)` | 无单次原生分页导出 | `vector<A2Gid> aria2::getActiveDownloadId()`<br/>`aria2::getDownloadHandle(Session*, A2Gid)` | 上游 libaria2 原生无 `tellWaiting` / `tellStopped` 分页接口 | **引擎层任务注册表方案**：宿主基于事件回调与周期快照在托管层维护一致性任务注册表，提供纯内存切片与单调 `Revision`，彻底突破 RPC 固定 100 条截断限制 |
| **操作结果查询**<br/>`GetOperationOutcomeAsync(opId)` | 无原生导出 | 原生调用在 owner 线程执行为纯同步 | 上游无异步操作记录与去重查询能力 | **宿主层环形结果缓存方案**：宿主为每个命令分配单调 `OperationId`，在有界环形缓存（1,024条，10分钟TTL）中记录 `Pending/Completed/Failed/Unknown`，解耦调用方超时与后台执行 |
| **事件通知**<br/>`WatchEventsAsync()` | `a2_engine_poll_events` | `DownloadEventCallback::onDownloadEvent` | 原生回调仅向有界紧凑无锁环形队列写入定宽结构体，由 owner 线程统一出队并写入 C# `Channel<EngineEvent>` | 回调中严禁抛异常、严禁调用托管代码、严禁重入 libaria2 |

核心命令、查询、关闭和恢复映射已全部明确锁定，阶段 0 正式达成退出条件。

## 4. 生命周期与调度

### 4.1 状态契约

正常路径为 `Created → Starting → Ready → Stopping → Stopped`。Starting、Ready、Stopping 遇到致命引擎错误均可进入终态 Faulted。

- 重复启动在 Starting 时共享启动结果，在 Ready 时成功返回；Stopped/Faulted 后不复用该 host。
- 业务命令仅在 Ready 接纳，其他状态立即返回明确状态错误。
- 就绪要求初始化、session 创建、恢复加载和首次运行推进成功，且 session 可继续接纳任务；不能把空任务自然结束误判为 Ready。
- 必须配置 `keepRunning=true`：官方默认 `keepRunning=false` 时无任务即 `run()` 返回 0（等同无 RPC 的 aria2c 行为），会被误判为正常结束；本应用要求空任务常驻 Ready，具体行为以锁定版本探针验证为准。
- 信号由应用接管：关闭 libaria2 默认信号处理（`useSignalHandler=false`，按锁定版本字段名验证），SIGTERM/退出统一走应用关闭流程，避免与 Avalonia 生命周期抢信号；具体设置按锁定版本验证。
- Starting 中关闭：记录停止请求，不再发布 Ready；当前 native 调用返回后按初始化进度清理，启动请求以“启动被关闭”结束。
- Created 中关闭直接进入 Stopped。重复关闭共享同一结果，包括首次关闭的失败结果。

### 4.2 运行循环

`RUN_ONCE` 不等于非阻塞调用。官方示例说明它可等待一次事件轮询，默认超时约一秒——这直接影响命令响应延迟和 500ms 就绪目标（§4.1 的循环调度依赖此外提）。因此循环设计在阶段 1 探针结论出来前不冻结：先用最小探针确定锁定版本的轮询等待策略、空闲 CPU 和命令延迟，再定批量/时间预算与采样频率。

阶段 1 探针必须回答（写入受版本控制的运行配置，调优参数不改变契约）：

1. 实际轮询 API 与等待控制手段：锁定版本是否支持缩短/中断轮询等待的参数或调用；Channel 唤醒能否中断正在执行的 native 调用（默认假设：不能）。
2. `keepRunning=true` 下空 session 的 `RUN_ONCE` 返回值与 CPU 行为：是否常驻、空闲 CPU 多少。
3. 命令从入队到 native 开始执行的 p50/p95（含一次 `RUN_ONCE` 等待的影响）。
4. 关闭信号在 `RUN_ONCE` 等待中的响应延迟。

约束：不能以忙轮询满足延迟目标；不能假设 Channel 唤醒可中断 native 调用；无法兼顾延迟与 CPU 时记录瓶颈并停止扩大迁移，不增加跨线程 native 调用或端口。[上游说明](https://aria2.github.io/manual/en/html/libaria2.html)

owner 每轮：

1. 执行有限批量命令，同时受本轮耗时预算限制，不无限排空后才推进下载。
2. 调用一次 `RUN_ONCE`，检查结果；未请求停止时意外结束必须暴露原因。
3. 复制状态事件，到期采样进度和快照，释放全部临时 handle。
4. 如需额外等待，仅等待下一轮截止时间或命令/关闭信号；native 已长时间等待后不再无条件 sleep。

轮询等待上限、批量/时间预算、采样频率由阶段 1 探针结论填入运行配置。首次 `RUN_ONCE` 的等待计入 engine Ready 耗时。可以另行记录“session 创建完成／首次轮询调用开始”的时间点，但不得替代 Ready、提前完成 `StartAsync`，或用于宣称达到 500ms 就绪目标。若实测超过目标，在 M01 如实记录原因；500ms 仍为试验目标，不单独阻止正确性阶段退出。它们是调优参数，不改变契约。不能以忙轮询满足延迟目标；无法兼顾时记录瓶颈并停止扩大迁移，不增加跨线程 native 调用或端口。

### 4.3 关闭与失败清理

1. 原子关闭接纳入口，终止尚未接纳的等待者；关闭信号不占用可能已满的命令队列。
2. 在配置的关闭期限内处理已接纳命令，期间持续推进循环；已取消且未执行的命令不产生副作用。
3. owner 请求 libaria2 shutdown，并推进到正常结束，再执行 `sessionFinal`、`libraryDeinit`，最后完成线程退出。
4. 期限到达时未开始的命令以关闭超时结束；如锁定版本支持且验证过，可在 owner 请求强制 shutdown，该路径必须报告非正常关闭。

不得用线程中止、另一线程 destroy 或假成功处理 native 卡死。进程内无法保证中断任意卡死调用；watchdog 记录故障并交给应用终止策略，不能复用 session。

初始化失败按“library 已初始化 / session 已创建”逆序清理，不释放未创建或已释放资源。Faulted 后停止正常调用，完成尚未结束的请求并保留根因，只执行已证明安全的清理；内存损坏类故障终止进程。清理失败作为附加诊断，不覆盖根因。`sessionFinal` 返回值按锁定版本退出状态解释，不把历史下载失败自动判成内存损坏。

## 5. C ABI 与错误契约

### 5.1 数据与所有权

- 导出覆盖创建、运行、命令、查询、事件读取和销毁，以版本管理，不追求固定五个函数。
- 结构体含 `size`、`version`，使用定宽整数、字节指针和长度；固定调用约定、布局、对齐、长度单位和上限。STL、C++ 异常、bool、wchar_t 不跨边界。
- 文本为带长度 UTF-8，调用期复制输入输出。托管侧只保留 GID、操作 ID 和独立 DTO，不持有 native 所有权。
- `DownloadHandle` 由 bridge RAII 管理，所有路径在下一次 run 前释放。有状态 ABI 入口校验 owner 线程，错误线程不触碰 session。
- 托管使用 `LibraryImport`，启动检查 ABI 版本，不依赖运行时反射封送。

### 5.2 缓冲区协议不重放写操作

- 写命令返回固定结果头，例如状态、GID 和诊断编号；先验证输出容量再执行副作用，不能为取错误文本重放原命令。
- 可变长查询使用调用方缓冲区；`BUFFER_TOO_SMALL` 返回所需容量且无业务副作用，是正常协议状态。
- 事件读取容量不足不出队，复制成功才消费。两阶段读取期间不能插入 run 或其他改变该批数据的操作；必要时仅冻结一批有界结果。
- 诊断文本单独读取，保持到下一次有状态 ABI 调用；内存不足仍能返回固定错误码。尺寸异常、超限明确失败，不无限扩容。
- 未预期 C++ 异常在 ABI 边界转 fatal，不穿出 ABI。回调不抛异常，溢出记 fatal 标记，返回 owner 后处理。

### 5.3 fail-fast 的行为

| 类别 | 行为 |
|---|---|
| 下载错误，如 HTTP 失败 | 保留任务错误码并更新 UI，其他下载继续；aria2 自身按用户配置执行协议重试。 |
| 命令错误，如非法 GID、选项、状态 | 立即失败该请求，不重试、不假成功；session 完整时处理其他请求。 |
| 满载、未就绪、请求过期 | 明确拒绝并说明是否已接纳，不静默丢请求。 |
| ABI 不匹配、内存/协议不变量损坏、未预期 native 异常、关键事件溢出 | Faulted，停止接纳并完成未决请求，不自动重启。 |
| 网关非法来源、坏帧、慢连接 | 拒绝请求或关闭该连接并报告原因，不让外部坏请求直接击穿引擎。 |

命令级预期错误边界完成对应 TCS；owner 入口另设最终致命故障边界。允许有目的的错误转换和清理，不允许 blanket catch 返回空列表、false 或成功。

错误带操作名、操作 ID、可用 GID、原始错误码和根因。日志不记录 Cookie、Authorization、完整敏感 URL query 或请求原文。

## 6. 有界并发、取消与事件

- 接纳成功是命令原子进入队列并登记结果；关闭与接纳必须线性化，不能遗留无结果请求。
- UI 可异步等待有界 Channel，但批量生产必须顺序或有限并发，不能预建无限等待 Task。网关非阻塞接纳，满载立即 `QueueFull`，不积累写等待者。
- 命令使用单调 ID 和启用 `RunContinuationsAsynchronously` 的 TCS，结果只完成一次，调用方 continuation 不在 owner 执行。
- 接纳前或 native 开始前取消保证不执行；开始后只返回真实结果，不承诺中断。取消/开始有唯一竞态判定点。
- 超时只结束等待，不代表未执行；保留操作 ID 供查询，禁止因响应丢失自动重新添加。
- 原生队列保存紧凑状态转换；进度定时采样、按 GID 合并，UI 快照采用有界最新值槽位。
- 关键状态通道独立有界，进度不能覆盖它。慢订阅者不阻塞 owner；超限显式结束该订阅并报错，UI 显示更新中断并可请求完整快照。
- native 关键队列溢出代表事件完整性失效，按 D05 Faulted，不能无依据改成仅告警。
- 快照带 host 实例 ID 与修订号，旧实例延迟 UI 更新必须丢弃，无需恢复 WebSocket 重连系统。

所有容量、期限、采样频率集中于一份运行配置，启动校验范围。阶段 1 固定数值和负载；测试引用配置或主动缩小容量触发边界，不把随意魔数变成业务契约。

## 7. 持久化与既有业务保护

- 显式指定 session 文件、恢复策略、下载目录和间隔；默认保留现有 30 秒周期保存，正常关闭再次保存，不依赖开发机 aria2 配置。
- session 任务清单和 `.aria2` 进度文件分别验证。崩溃只恢复最后成功落盘状态，不承诺零丢失或完整历史列表持久化。
- 状态文件缺失可视为首次运行；存在但损坏、不可读或不可写必须显示路径和原因，不偷偷删除、覆盖或作为空清单。启动加载失败则启动失败；运行中确定的会话保存失败使引擎 Faulted，停止接纳并报告无法继续保证恢复状态。仅在用户显式选择备份后重置时创建新状态。
- 配置保存与内核应用分别表达，不能回归 REVIEW #50；需重启设置标记待下次启动，不自动重建 session。
- 保留 Tracker 校验、取消、资源释放和文件删除路径保护；多任务添加、删除文件不承诺跨组件事务。
- 历史清理、磁链后续 GID、分页、实际选项读取的缺失能力在阶段 0 明确方案，不篡改内核弥补 UI 预期。

## 8. 浏览器接管：受限 Native Messaging

### 8.1 支持范围

本期实现并验收 Linux 上一种明确记录版本的 Chromium 系浏览器与专用扩展，其他浏览器单独扩展验收。安装成功不能替代真实接管测试。

唯一链路为“扩展 → Native Messaging stdio → 薄宿主 → Unix Socket → 应用服务”。不提供 loopback HTTP、WebSocket、TCP、custom scheme 或启动失败后的备用通道。仅开放 `AddDownload`、`GetRequestResult`，不开放任意 RPC、删除文件、全局选项、shell 或下载内容代理。

首批接管 aria2 可重放的 HTTP/HTTPS GET 与 magnet。POST、blob、data、浏览器内存流、DRM 和依赖页面运行状态的下载明确不支持，交回浏览器原流程。宿主不自行下载种子或实现 Cookie 修复。

### 8.2 来源与本机边界

- manifest 固定宿主绝对路径和 `allowed_origins`；校验启动 origin，但不把可伪造 argv 当成本地进程密码。
- socket 位于已校验所有权的本用户运行目录，私有目录 0700、socket 0600；两端检查 Linux peer credentials 的 UID。拒绝路径替换、符号链接和其他用户连接，不用全局可写 socket。
- 安全边界为当前 OS 用户与扩展白名单，不声称隔离同 UID 恶意程序。无需新共享 secret，仍须验证来源、权限和输入。
- 锁由应用持有。连接不上 socket 不等于应用没启动；锁占用通常表示已有实例。宿主最多请求一次应用启动，由应用原子争锁，竞争失败者退出，宿主在有期限握手中等待现有实例 Ready。
- 启动应用不继承宿主 stdout 协议流。只有持锁应用可安全清理其验证过的旧 socket；宿主不删锁、不杀进程、不循环启动。权限错误立即报告，不当成首次启动。

### 8.3 协议、容量与背压

固定 `connectNative` 单连接顺序请求模式：宿主随 port 存活，断开后退出；只用一个受限 Unix Socket 连接，不建自己的任务池或持久下载队列。未来改 `sendNativeMessage` 必须单独验收逐消息起进程行为。[浏览器协议](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)

- 帧遵循 32 位本机字节序长度 + UTF-8 JSON，本期 x64 为小端。长度按字节计，分配前查上限，读满再解析；EOF、半帧超时、非法 UTF-8/JSON 明确失败。stdout 仅协议，日志走 stderr。
- 主动限制双向单帧 64 KiB、每连接一个未决请求、应用最多 8 个网关连接、每扩展每秒 10 个新添加请求（突发 10）、初始请求期限 10 秒。这些是项目限额，不是浏览器双向统一 1MB 限制。
- 不传种子 base64、文件内容或分片大数据。schema 固定版本、操作、客户端 requestId、应用实例 ID 和载荷，拒绝未知版本、操作、字段及超限内容。
- 满载在接纳前返回 `QueueFull` 或 `RateLimited`，不能同时承诺等待与立即拒绝。慢连接到期关闭，不持续读 stdin 后无限排队。
- 每次 AddDownload 添加一个任务，结构化 URL 和选项白名单仅为 `dir`、`out`、`referer`、`header`、`user-agent`。应用按保存目录策略校验路径，out 禁止目录穿越，不透传任意内核选项。
- header 为有界数组，一项一条，禁止 CR/LF/NUL 注入；转换为 aria2 KeyVals，不拼 Cookie、不补重定向行为。Cookie/Authorization 只用于用户允许的目标下载，不写日志。

### 8.4 结果与重复提交

- 握手返回应用实例 ID。去重键为“实例 ID + 扩展 ID + requestId”；同 ID 不同载荷冲突，处理中返回 Pending，完成后返回原结果，不重复执行。
- 请求记录上限 1,024 条，终态保留 10 分钟，处理中不得淘汰；满载先拒绝新请求。过期查询返回 `UnknownOutcome`，不能自动重放；添加必须匹配当前实例 ID。
- GetRequestResult 仅查询本扩展请求。断开/超时后先查原 ID，不自动换 ID 重加；重启或过期后的未知结果提示用户结合列表确认。不承诺跨崩溃 exactly-once，不增加持久消息队列。
- 真实 GID 才代表添加成功；入队、Pending、宿主写出请求不是成功。收到成功后才确认取消浏览器原下载，等待期间按浏览器能力暂挂。
- 明确拒绝或不支持则恢复浏览器原流程并提示原因；结果未知时保留原任务信息并提示待确认，不擅自同时恢复和重加。不能暂挂/恢复的下载类别不纳入自动接管。

## 9. 构建与分发

- 锁定 commit、依赖、补丁与校验值。bridge 用 CMake preset，上游用其支持的构建系统，由脚本串联，不假设 aria2 本身是 CMake 项目。
- 本期优先捆绑 bridge、libaria2 及必需的非系统共享库，明确 Linux/glibc 最低基线、加载路径和 TLS 信任库来源；依赖按启用特性生成，不机械要求全部可选库。
- native 资产按 `runtimes/linux-x64/native/` 纳入发布，验证实际输出与依赖解析；不依赖预装 aria2，不在运行时联网补库。
- 干净 CI 构建 Debug、Release、Native AOT；归档工具链、参数、SBOM、校验和及独立调试符号。
- 项目接受 GPL 兼容分发方向。仅修改文档时保留 MIT 文件；第一次向他人分发 native-linked 产物（含可下载 CI 产物）前完成许可证切换方案、版权/第三方声明、对应源码、补丁与构建材料。最终表述以锁定源码和依赖审查为准，这是分发门禁，不能拖到删除 RPC 后处理。

## 10. 实施顺序与退出条件

| 阶段 | 工作与退出条件 |
|---|---|
| 0：契约与基线 | 固定能力映射、版本/依赖；建立仓库测试项目，迁入相关历史回归；记录 RPC 指标。映射未完成不迁 UI。 |
| 1：原生探针 | 验证空 session 常驻、同线程、轮询、活动下载关闭、初始化失败和恢复，固定运行配置。生命周期/ABI 核心测试通过再扩大接入。 |
| 2：核心迁移 | 按能力实现应用服务边界、命令、分页、选项、事件和恢复；RPC 功能冻结，仅构建时对照，逐项通过共同契约，不要求未实现能力提前通过。 |
| 3：浏览器网关 | 实现扩展、宿主、socket、manifest，真实验证接管成功、拒绝、未知结果和冷启动；不为兼容旧扩展开端口。 |
| 4：候选验收 | Linux 功能、压力、24 小时稳定性、AOT、无控制端口、干净环境和分发材料全部通过，native 成默认。 |
| 5：清理 | 候选通过后删 RPC、进程管理、secret、回退开关，复跑删除影响的测试和发布冒烟，确认最终包不依赖 aria2c。 |

RPC 回退仅显式构建选择，不允许 native 失败时自动回退。迁移期间不开发 RPC 新功能；影响对照可信度的真实缺陷仍作最小修复。Windows 不得混入退出门禁。

500ms 本地冷启动是试验测量目标，不要求无限调优；样本、可比基线、硬门禁和停止规则由测试文档统一规定。

## 11. 变更规则与完成定义

D01–D08 固定设计方向；参数和实现可据测量调整，不能因重构偏好反复推翻边界。改契约须同一提交更新本文件、关联测试 ID 与理由，不另加互相冲突的“评审补充”。

完成要求：Linux 单一进程内引擎，生命周期/错误/内存/背压可测，浏览器受限接管成功，控制链路无 TCP/HTTP/WebSocket 端口，既有业务保护未回归，发布测试和源码/二进制交付齐全。未执行或未实现记为 Pending，不以文档承诺代替结果。

## 12. 上游依据

- [libaria2 官方说明](https://aria2.github.io/manual/en/html/libaria2.html)：session、串行访问、循环与 handle；固定 owner 线程是本项目更严格的工程约束。
- [aria2 构建说明](https://aria2.github.io/manual/en/html/README.html)：构建开关与依赖。
- [Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)：manifest、帧、方向性限制与生命周期。

在线文档用于定位，最终以锁定源码和集成测试为准。未经验证的上游 issue 不写成所有版本通用行为，不为本期边界外差异加补丁层。
