# AriaUI 内嵌 libaria2 架构规划

## 1. 决策与边界

- 目标平台仅为 Linux 和 Windows；不规划 macOS 支持。
- 新架构把 aria2 作为进程内原生库链接，移除 `aria2c` 子进程、WebSocket JSON-RPC、端口探测与认证握手。
- 项目接受 libaria2 所要求的 GPL-2.0 兼容发布方式，但在首个可分发的 native-linked artifact 落地前，当前代码仍保留 MIT，不提前修改 `LICENSE`。
- 首次分发链接 libaria2 的构建产物时，必须同步完成许可证切换、第三方声明、对应源码与可复现构建材料。最终 SPDX 标识以锁定版本 aria2 源码中的许可证文本为准。
- 每个进程只创建一个 aria2 session；不支持同进程多 session，也不提供旧 RPC 后端的长期兼容承诺。

## 2. 目标结构

```text
Avalonia Views / ViewModels
           │
           ▼
      IAriaEngine
           │ async commands / immutable snapshots
           ▼
 NativeAriaEngineHost
           │ bounded Channel
           ▼
 单一专用线程（唯一 native owner）
           │ stable C ABI
           ▼
 ariaui_native_bridge
           │ C++ API
           ▼
        libaria2
```

关键约束：

- UI 和 ViewModel 只依赖 `IAriaEngine`，不得出现 P/Invoke、native handle 或 aria2 C++ 类型。
- `NativeAriaEngineHost` 拥有专用线程、命令队列、生命周期状态和未完成请求。
- session 初始化、所有 aria2 操作、`RUN_ONCE` 事件循环及关闭必须发生在同一专用线程。
- C# 不直接链接 aria2 的 C++ ABI；中间层只导出小而稳定的 C ABI。
- 不把 `aria2::DownloadHandle*`、STL 容器、C++ 异常或 native 所有权暴露给托管层。

## 3. 托管接口与状态机

`IAriaEngine` 首批能力：

- `StartAsync`、`ShutdownAsync`
- `AddUriAsync`、`AddTorrentAsync`
- `PauseAsync`、`ResumeAsync`、`RemoveAsync`
- `ChangeGlobalOptionsAsync`、`ChangeTaskOptionsAsync`
- `GetGlobalSnapshotAsync`、`GetTaskSnapshotAsync`、`GetTaskListAsync`
- 任务变化事件流（新增、进度、暂停、完成、错误、移除）

生命周期严格为：

```text
Created → Starting → Ready → Stopping → Stopped
                    ↘ Faulted
```

- `StartAsync` 在专用线程完成 `libraryInit`、`sessionNew` 和首次成功的 `RUN_ONCE` 后才完成；这就是唯一的“服务就绪”定义。
- `Starting` 或 `Faulted` 状态收到业务命令时立即失败，不做等待重试或隐式重启。
- 致命 native 错误使状态原子切换到 `Faulted`，所有排队和执行中的请求以同一个根因异常结束。
- `ShutdownAsync` 停止接收新命令，排空已接受命令，执行 `sessionFinal`、`libraryDeinit`，最后完成线程退出；重复调用应幂等。

专用线程循环的固定顺序为：

1. 排空一批已入队命令并逐条执行。
2. 调用一次非阻塞 `RUN_ONCE`。
3. 从 native 事件队列复制事件并发布不可变快照。
4. 若无工作，以有限等待阻塞到命令、事件循环截止时间或关闭信号。

不得在 UI 线程、线程池回调或事件订阅者中调用 libaria2。

## 4. C ABI 与内存契约

原生桥接层使用版本化导出，例如：

```c
ariaui_status ariaui_engine_create(const ariaui_engine_options* options);
ariaui_status ariaui_engine_run_once(void);
ariaui_status ariaui_engine_execute(const ariaui_command* command,
                                    ariaui_result_buffer* result);
ariaui_status ariaui_engine_drain_events(ariaui_event_buffer* events);
ariaui_status ariaui_engine_destroy(void);
```

ABI 规则：

- 所有结构体以 `size`、`version` 开头，只使用定宽整数、字节指针和长度；禁止 `bool`、`wchar_t`、STL 类型跨边界。
- 文本统一为 UTF-8，并始终携带显式长度，不依赖 NUL 终止。
- 字符串、数组和 DTO 在调用返回前复制到调用方提供的缓冲区；需要扩容时返回所需大小，由托管方重新分配后重试。
- 任何 native 指针仅在当前 ABI 调用期间有效，托管层不得缓存。托管侧只持有 GID、命令 ID 等值类型标识。
- DTO 是只读快照，不允许托管层直接修改 aria2 内部对象。
- C++ 异常只允许在最外层 C ABI 边界被转换为明确的 fatal status；不得吞掉异常或伪造默认业务结果。

事件回调规则：

- aria2 回调只把紧凑事件复制进预分配的 native 有界队列，不执行业务逻辑，不调用托管代码，也不重入 aria2。
- 高频进度事件允许按 GID 合并；完成、错误、移除等状态转换不得静默丢弃。
- 不可合并事件溢出视为致命错误并使 engine `Faulted`，从而尽早暴露容量或消费速度问题。

## 5. 并发、取消与背压

- 托管命令进入有界 `Channel`；容量必须显式配置，满载时生产者异步等待，不创建无限队列。
- 每条命令包含单调递增 ID 和一个 `TaskCompletionSource`，结果只能完成一次。
- 命令出队前可取消；开始 native 调用后不承诺中断，必须返回真实结果或真实错误，避免状态不明。
- 调用方超时只结束该调用方的等待，不得悄悄重启 session 或伪造 native 取消。
- 事件订阅者运行在专用线程之外；慢订阅者不能阻塞事件循环。
- 快照进入 UI 前合并更新，避免每个下载字节变化都触发 UI 调度。

## 6. 错误模型

- C ABI 返回版本化 `ariaui_status`，包含类别、aria2/native 错误码和诊断文本所需长度。
- 非零 status 在 `NativeAriaEngineHost` 中转换为带操作名、命令 ID、GID 和原始错误码的类型化异常，并向上抛出。
- 参数错误、ABI 版本不匹配、非法状态和缓冲区协议错误立即失败。
- 不使用宽泛 `catch` 包裹业务流程，不把异常转换为空列表、`false` 或成功状态。
- 仅在线程入口保留最终故障边界，用于完成待处理任务、记录根因并进入 `Faulted`；它不得继续运行受损 session。

## 7. 构建、打包与许可证

- 锁定 aria2 的明确 tag 与 commit；优先以 Git submodule 引入，并记录所有 native 依赖版本、补丁与校验值。
- `ariaui_native_bridge` 使用 CMake presets 构建；初始发布 RID 仅为 `linux-x64` 和 `win-x64`，其他 Linux/Windows 架构需单独通过验收后增加。
- 产物按 `runtimes/<rid>/native/` 打包为 `.so` / `.dll`；C# 使用 source-generated `LibraryImport`，启动时校验 ABI 版本。
- CI 必须从干净环境构建 native bridge、libaria2 和 Native AOT 应用，不依赖开发机已安装的 aria2。
- 发布记录保存工具链版本、构建参数、依赖清单、校验和及 SBOM；调试符号单独归档。
- 首个链接产物合入前设置许可证门禁：更新根许可证与 README，保留版权声明，附 aria2 及其依赖的许可证，并提供 GPL 要求的对应源码、补丁和构建脚本。

## 8. 迁移阶段与退出门槛

### 阶段 0：冻结契约与基线

- 从现有 RPC 行为提取 `IAriaEngine` 契约测试和错误语义。
- 记录启动耗时、命令延迟、刷新开销及内存基线。
- 确认锁定 aria2 版本的许可证、构建选项和目标平台依赖。

退出门槛：接口评审通过，现有 RPC 实现通过全部契约测试。

### 阶段 1：native 最小探针

- 构建 C ABI bridge，完成同线程 init、session、一次 `RUN_ONCE` 和 shutdown。
- 实现 ABI 版本检查、错误文本协议和最小事件队列。

退出门槛：Linux/Windows 均可连续启动关闭 1,000 次；无死锁、残留线程或 native 泄漏。

### 阶段 2：引擎抽象与双后端

- ViewModel 改为只依赖 `IAriaEngine`。
- 暂时保留 RPC 与 native 两个实现，仅用于迁移对照和回滚，不在 UI 中暴露长期后端选择。

退出门槛：两后端通过同一套契约测试，UI 不再引用 RPC 具体类型。

### 阶段 3：核心命令与快照

- 迁移添加、暂停、恢复、移除、选项和任务查询。
- 落实有界 Channel、取消语义、DTO 所有权及任务事件合并。

退出门槛：核心工作流在真实 libaria2 集成测试中通过，错误无静默降级。

### 阶段 4：事件、恢复与持久化

- 覆盖完成/错误/移除事件、session 保存恢复、全局状态和应用关闭排空。
- 验证崩溃后恢复与损坏状态文件的 fail-fast 行为。

退出门槛：强制退出、重启恢复、批量任务和慢订阅者压力测试通过。

### 阶段 5：平台与性能验收

- 对 Linux/Windows 做 Native AOT、长时运行、并发任务和大列表验证。
- 与阶段 0 基线比较；就绪目标为本地冷启动 500 ms 内，常用命令 p95 不高于 RPC 基线，稳定态内存不得持续增长。

退出门槛：所有发布门禁通过，native 后端成为默认；仍可通过构建开关回退 RPC。

### 阶段 6：GPL 发布切换与清理

- 完成许可证与源码交付材料后，删除 RPC、子进程管理、端口探测、secret 配置和双后端开关。
- 发布候选包并验证安装目录不依赖外部 `aria2c`。

退出门槛：GPL 合规检查完成，Linux/Windows 安装包和对应源码可复现构建，旧后端代码全部移除。

## 9. 测试门禁

- ABI：结构体尺寸/对齐、版本不匹配、UTF-8、零长度、两阶段缓冲区和错误文本测试。
- 生命周期：初始化失败、首次 `RUN_ONCE` 失败、重复关闭、关闭中请求、fatal fault 传播。
- 并发：队列满载、取消竞态、事件洪峰、慢消费者、批量 1,000 个任务。
- 功能：使用本地 HTTP/BitTorrent 测试源验证添加、进度、暂停、恢复、完成、移除和恢复。
- 稳定性：至少 24 小时循环运行；ASan/UBSan（Linux）和 Windows native 内存诊断无新增问题。
- 打包：干净机器/容器离线启动；不存在 `aria2c` 子进程、RPC 监听端口或运行时下载依赖。
- 发布：Debug、Release、Native AOT、许可证清单、对应源码包和校验和全部通过。

任何门禁失败都阻止删除 RPC 回退实现；不得用自动重试掩盖失败。

## 10. 主要风险与控制

- **进程内崩溃扩大影响**：缩小 C ABI，开启 sanitizers，保留可符号化崩溃信息；不尝试在崩溃后复用 session。
- **aria2 C++ API 变化**：锁定 commit，所有适配集中在 bridge，升级必须重新跑 ABI 与契约测试。
- **事件循环饥饿**：限制单轮命令批量和 `RUN_ONCE` 间隔，压力测试测量最大事件延迟。
- **回调重入或悬空指针**：回调只复制事件，禁止托管回调和跨调用保存指针。
- **Windows 构建复杂度**：固定工具链与 CMake preset，在 CI 中从源码构建，禁止手工预装依赖。
- **GPL 交付遗漏**：把许可证、对应源码和构建复现检查设为发布流水线硬门禁。

## 11. 完成定义（DoD）

- Linux/Windows 上应用只通过 `IAriaEngine` 使用一个进程内 libaria2 session。
- 初始化、`RUN_ONCE`、全部命令和关闭由同一专用线程执行，就绪与故障状态确定且可观测。
- 没有 WebSocket RPC、端口探测、认证 secret、`aria2c` 子进程或裸 C++ 指针跨层。
- 内存所有权、事件背压、取消和错误传播均有自动化测试，且所有错误保持 fail-fast。
- Debug、Release、Native AOT、集成、压力和长时测试通过 Linux/Windows CI。
- GPL 许可证、第三方声明、对应源码、补丁、构建脚本、SBOM 和校验和与二进制同时可获得。
- RPC 回退实现仅在上述条件全部满足后删除。
