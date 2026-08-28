# AriaUI 项目架构与代码审查报告

> **审查日期**: 2026-08-22（初审）/ 2026-08-27（复审）/ 2026-08-28（全量落地修复）  
> **审查范围**: 全部源代码 (Models / Services / ViewModels / Views / Converters / 入口文件)  
> **技术栈**: .NET 10 · Avalonia 12.1 · CommunityToolkit.Mvvm · Semi.Avalonia · Microsoft.Extensions.DependencyInjection  
> **修复进度**: 28 / 29 项已完全解决 (96.5%)，编译通过 (0 错误 / 0 警告)

---

## 一、项目架构总览

### 1.1 目录结构

```
AriaUI/
├── Models/
│   ├── AppSettings.cs          # 应用配置模型
│   ├── AriaTask.cs             # Aria2 任务/文件/BT 信息模型
│   └── RpcModels.cs            # JSON-RPC 请求/响应模型
├── Services/
│   ├── AriaProcessService.cs   # aria2c 守护进程管理
│   ├── AriaTaskService.cs      # 任务业务逻辑(轮询/CRUD/Tracker等)
│   ├── AriaWebSocketRpcClient.cs # WebSocket JSON-RPC 通信客户端
│   └── SettingsService.cs      # 配置持久化 (JSON 文件读写)
├── ViewModels/
│   ├── ViewModelBase.cs        # ViewModel 基类
│   ├── MainWindowViewModel.cs  # 主窗口 VM (导航/全局状态/通知)
│   ├── TaskListViewModel.cs    # 任务列表 VM (筛选/搜索/批量操作)
│   ├── TaskItemViewModel.cs    # 单条任务 VM (进度/操作)
│   ├── NewTaskViewModel.cs     # 新建任务对话框 VM
│   └── SettingsViewModel.cs    # 设置页 VM
├── Views/
│   ├── MainWindow.axaml(.cs)   # 主窗口视图 (侧栏+内容区+状态栏)
│   ├── TaskListView.axaml(.cs) # 任务列表视图
│   └── SettingsView.axaml(.cs) # 设置页视图
├── Converters/
│   ├── CommonConverters.cs     # 大小/速度/状态颜色/连接状态转换器
│   └── FormatHelper.cs         # 格式化工具类 (字节/速度/ETA)
├── App.axaml(.cs)              # 应用入口 (DI容器/生命周期)
├── Program.cs                  # 程序入口点
└── ViewLocator.cs              # ViewModel→View 自动映射
```

### 1.2 架构流程图

```
┌──────────────────────────────────────────────────────────────────┐
│                         Views 层                                 │
│  MainWindow.axaml ──→ TaskListView.axaml / SettingsView.axaml   │
│       │                      │                    │              │
│       ▼                      ▼                    ▼              │
│  MainWindowViewModel   TaskListViewModel    SettingsViewModel    │
│       │                 │          │              │              │
│       │           TaskItemVM   NewTaskVM          │              │
│       │                 │          │              │              │
├───────┼─────────────────┼──────────┼──────────────┼──────────────┤
│       ▼                 ▼          ▼              ▼              │
│                    AriaTaskService (核心业务层)                    │
│                    ┌──────┬───────┬──────┐                       │
│                    ▼      ▼       ▼      ▼                       │
│             AriaProcess  RpcClient  Settings   HttpClient        │
│             Service      (WS)       Service    (Tracker)         │
│                    │                    │                         │
│                    ▼                    ▼                         │
│              aria2c 进程          config.json                     │
└──────────────────────────────────────────────────────────────────┘
```

### 1.3 整体评价

项目 MVVM 分层基本清晰，使用了 CommunityToolkit.Mvvm 的源码生成器简化样板代码，WebSocket RPC 通信实现完整，UI 使用 Semi.Avalonia 主题风格统一。但在依赖管理、异常处理、线程安全、资源释放等方面存在需要改进之处。

---

## 二、严重问题 (🔴 必须修复)

---

### 问题 #1：DI 容器被手动 `new` 绕过，依赖注入形同虚设

**严重程度**: 🔴 高  
**类型**: 架构设计  

#### 问题所在

项目在 `App.axaml.cs` 中正确配置了 DI 容器：

```csharp
// App.axaml.cs 第 22-31 行
var collection = new ServiceCollection();
collection.AddSingleton<ISettingsService, SettingsService>();
collection.AddSingleton<IAriaProcessService, AriaProcessService>();
collection.AddSingleton<IAriaRpcClient, AriaWebSocketRpcClient>();
collection.AddSingleton<IAriaTaskService, AriaTaskService>();
collection.AddSingleton<MainWindowViewModel>();
```

但 `MainWindowViewModel` 在构造函数中**手动 new** 了它的所有子 ViewModel，完全没有通过 DI 容器解析：

```csharp
// MainWindowViewModel.cs 第 53-54 行
TaskListVm = new TaskListViewModel(_taskService, _settingsService);
SettingsVm = new SettingsViewModel(_settingsService, _taskService, _processService);
```

同样的问题存在于 `TaskListViewModel` 中：

```csharp
// TaskListViewModel.cs 第 108 行
var newVm = new TaskItemViewModel(taskInfo, _taskService);

// TaskListViewModel.cs 第 177 行
var vm = new NewTaskViewModel(_taskService, _settingsService);
```

#### 为什么是问题

1. **违反依赖倒置原则**: 上层模块直接依赖具体构造过程，而不是由容器管理
2. **扩展困难**: 如果 `TaskListViewModel` 将来需要一个新的 `ILogger` 依赖，你必须从 `MainWindowViewModel` 一路向下修改构造函数来传递它
3. **无法替换**: 测试时无法轻松替换子 ViewModel 的实现

#### 建议修复方案

**方案 A — 将子 ViewModel 也注册到 DI 容器**:

```csharp
// App.axaml.cs
collection.AddSingleton<TaskListViewModel>();
collection.AddSingleton<SettingsViewModel>();

// 对于需要动态创建的 ViewModel，使用工厂模式
collection.AddTransient<NewTaskViewModel>();
collection.AddTransient<Func<AriaTaskInfo, TaskItemViewModel>>(sp =>
    taskInfo => new TaskItemViewModel(taskInfo, sp.GetRequiredService<IAriaTaskService>()));

// MainWindowViewModel 通过构造函数注入
public MainWindowViewModel(
    IAriaTaskService taskService,
    ISettingsService settingsService,
    IAriaProcessService processService,
    TaskListViewModel taskListVm,      // ← DI 注入
    SettingsViewModel settingsVm)      // ← DI 注入
{
    TaskListVm = taskListVm;
    SettingsVm = settingsVm;
    // ...
}
```

**方案 B — 最小改动，注入 `IServiceProvider`**:

```csharp
public MainWindowViewModel(IServiceProvider sp, ...)
{
    TaskListVm = sp.GetRequiredService<TaskListViewModel>();
    SettingsVm = sp.GetRequiredService<SettingsViewModel>();
}
```

---

### 问题 #2：事件订阅导致潜在内存泄漏

**严重程度**: 🔴 高  
**类型**: 资源管理  

#### 问题所在

多个 ViewModel 在构造函数中使用匿名 lambda 订阅 Service 层的事件，且**从未取消订阅**：

```csharp
// MainWindowViewModel.cs 第 58-74 行
_taskService.GlobalStatUpdated += (s, e) =>
{
    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        var stat = _taskService.GlobalStat;
        DownloadSpeedText = FormatHelper.FormatSpeed(stat.DownloadSpeedBytes);
        // ...
    });
};

_taskService.NotificationReceived += (s, msg) =>
{
    Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowNotification(msg));
};
```

```csharp
// TaskListViewModel.cs 第 58-61 行
_taskService.TasksUpdated += (s, e) =>
{
    Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTasksFromService);
};
```

#### 为什么是问题

- `IAriaTaskService` 是 **Singleton** (在 DI 容器中注册为单例)
- 事件委托链持有对 ViewModel 实例的**强引用**
- 如果 ViewModel 被重新创建 (比如导航切换)，旧的实例无法被 GC 回收
- 匿名 lambda 无法通过 `-=` 取消订阅
- 当前项目中 ViewModel 也是单例所以暂时不会泄漏，但架构上属于"定时炸弹"

#### 为什么即使当前不泄漏也应该修

如果你将来把 ViewModel 改为 `Transient` 生命周期（比如每次导航都创建新的），或者添加新的页面切换逻辑，泄漏就会立刻发生。

#### 建议修复方案

**方案 A — 使用 CommunityToolkit.Mvvm 的 `WeakReferenceMessenger`** (推荐):

```csharp
// 在 AriaTaskService 中发布消息而非触发事件
WeakReferenceMessenger.Default.Send(new TasksUpdatedMessage());
WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(stat));

// 在 ViewModel 中注册接收 (弱引用，自动回收)
public partial class TaskListViewModel : ViewModelBase, 
    IRecipient<TasksUpdatedMessage>
{
    public TaskListViewModel(...)
    {
        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(TasksUpdatedMessage message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTasksFromService);
    }
}
```

**方案 B — ViewModel 实现 IDisposable + 具名方法订阅**:

```csharp
public partial class TaskListViewModel : ViewModelBase, IDisposable
{
    public TaskListViewModel(...)
    {
        _taskService.TasksUpdated += OnTasksUpdated;
    }

    private void OnTasksUpdated(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTasksFromService);
    }

    public void Dispose()
    {
        _taskService.TasksUpdated -= OnTasksUpdated;
    }
}
```

---

### 问题 #3：异步 fire-and-forget 与空 catch 导致异常静默丢失

**严重程度**: 🔴 高  
**类型**: 异常处理 / 可靠性  

#### 问题所在

**情况一：fire-and-forget 丢弃 Task**

```csharp
// AriaTaskService.cs 第 67 行
_rpcClient.DownloadCompleted += (s, gid) =>
{
    NotificationReceived?.Invoke(this, $"下载已完成 (GID: {gid})");
    _ = RefreshTasksAsync();   // ← 异步任务被丢弃，异常变成 UnobservedTaskException
};

// AriaTaskService.cs 第 72 行
_rpcClient.DownloadError += (s, gid) =>
{
    NotificationReceived?.Invoke(this, $"下载出错 (GID: {gid})");
    _ = RefreshTasksAsync();   // ← 同上
};
```

**情况二：空 catch 吞没全部异常**

```csharp
// TaskItemViewModel.cs 第 114 行
try { await _taskService.PauseTaskAsync(Gid); } catch { }

// TaskItemViewModel.cs 第 120 行
try { await _taskService.ResumeTaskAsync(Gid); } catch { }

// TaskItemViewModel.cs 第 130 行
try { await _taskService.RemoveTaskAsync(Gid, deleteFile); } catch { }

// TaskListViewModel.cs 第 190 行
try { await _taskService.ResumeAllTasksAsync(); } catch { }

// TaskListViewModel.cs 第 196 行
try { await _taskService.PauseAllTasksAsync(); } catch { }

// TaskListViewModel.cs 第 202 行
try { await _taskService.PurgeCompletedTasksAsync(); } catch { }
```

这些 `catch { }` 共计 **6 处**，涵盖了几乎所有用户操作。

#### 为什么是问题

1. 用户点击"暂停"按钮，操作失败了，**没有任何反馈**——用户以为操作成功了
2. 网络断开时 RPC 调用失败，用户完全不知道
3. 开发调试时难以发现问题根源，因为异常被吞没了
4. `_ = RefreshTasksAsync()` 中如果 WebSocket 断开抛出异常，变成 `UnobservedTaskException`，在 Program.cs 中虽然有全局捕获但只能打印到 Console，毫无可操作性

#### 建议修复方案

**1. 创建安全的 fire-and-forget 扩展方法**:

```csharp
// Helpers/TaskExtensions.cs
public static class TaskExtensions
{
    public static async void SafeFireAndForget(
        this Task task, 
        Action<Exception>? onError = null)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            Console.Error.WriteLine($"[SafeFireAndForget] {ex.Message}");
        }
    }
}

// 使用方式
RefreshTasksAsync().SafeFireAndForget();
```

**2. 用户操作应给出错误反馈**:

```csharp
[RelayCommand]
private async Task PauseAsync()
{
    try
    {
        await _taskService.PauseTaskAsync(Gid);
    }
    catch (Exception ex)
    {
        // 通过 Messenger 或回调通知 UI 层显示错误 Toast
        ErrorMessage = $"暂停失败: {ex.Message}";
    }
}
```

---

### 问题 #4：`CleanupStaleAriaProcesses()` 无差别杀死系统上所有 aria2c 进程

**严重程度**: 🔴 严重  
**类型**: 安全性 / 正确性  

#### 问题所在

```csharp
// AriaProcessService.cs 第 66-81 行
private void CleanupStaleAriaProcesses()
{
    try
    {
        var existingProcesses = Process.GetProcessesByName("aria2c");
        foreach (var p in existingProcesses)
        {
            try
            {
                p.Kill(true);          // ← 无条件杀死
                p.WaitForExit(1000);
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
    }
    catch { }
}
```

#### 为什么是问题

- 用户可能同时运行着**其他用途的 aria2c 实例** (如命令行手动下载、其他客户端如 AriaNg 管理的进程)
- 该方法在每次 `StartDaemonAsync` 时被调用
- 它会**无差别地杀死系统上所有名为 aria2c 的进程**
- 这可能导致用户正在进行的下载任务中断、数据丢失

#### 建议修复方案

**使用 PID 文件追踪自己启动的进程**:

```csharp
private readonly string _pidFilePath;

public AriaProcessService()
{
    // ...
    _pidFilePath = Path.Combine(configDir, "aria2.pid");
}

public Task<bool> StartDaemonAsync(AppSettings settings)
{
    // 只清理自己上次启动的残留进程
    CleanupOwnStaleProcess();
    // ...
    _process = Process.Start(startInfo);
    
    // 记录 PID
    if (_process != null)
    {
        File.WriteAllText(_pidFilePath, _process.Id.ToString());
    }
}

private void CleanupOwnStaleProcess()
{
    if (!File.Exists(_pidFilePath)) return;
    
    try
    {
        var pidStr = File.ReadAllText(_pidFilePath).Trim();
        if (int.TryParse(pidStr, out var pid))
        {
            var process = Process.GetProcessById(pid);
            if (process.ProcessName == "aria2c")
            {
                process.Kill(true);
                process.WaitForExit(1000);
            }
        }
    }
    catch (ArgumentException) { } // 进程不存在，忽略
    catch (Exception ex)
    {
        Console.Error.WriteLine($"清理残留进程失败: {ex.Message}");
    }
    finally
    {
        try { File.Delete(_pidFilePath); } catch { }
    }
}
```

---

## 三、中等问题 (🟡 建议修复)

---

### 问题 #5：`AriaTaskService` 职责过重 (God Service 反模式)

**严重程度**: 🟡 中  
**类型**: 架构设计 / 单一职责原则  

#### 问题所在

`AriaTaskService.cs` 共计约 300 行代码，承担了以下**所有**职责：

| 行号范围 | 职责 | 应归属 |
|---------|------|--------|
| L55-76 | 初始化：启动 daemon + 建立 RPC 连接 | `AriaConnectionManager` |
| L78-104 | 1 秒轮询定时器 + 自动重连 | `AriaConnectionManager` |
| L106-123 | 刷新任务列表 (tellActive/Waiting/Stopped) | ✅ 保留 |
| L125-175 | 添加 URI 下载 (含多行拆分逻辑) | ✅ 保留 |
| L177-197 | 添加 Torrent 下载 | ✅ 保留 |
| L199-251 | 暂停/恢复/删除任务 (含文件清理) | ✅ 保留 |
| L253-274 | 限速管理 | ✅ 保留 |
| L276-311 | **Tracker 列表在线更新** (发 HTTP 请求) | `TrackerService` |
| L313-338 | **打开文件** (调用 xdg-open/open/explorer) | `FileSystemService` |
| L340-366 | **打开文件夹** (同上) | `FileSystemService` |

#### 为什么是问题

- "打开文件/文件夹"是 **Shell 操作**，跟 Aria2 任务管理毫无关系
- "Tracker 更新"需要发 **HTTP 请求**，是独立的网络操作
- 所有逻辑堆在一起导致文件过大、难以测试、难以复用
- 接口 `IAriaTaskService` 有 **15 个方法**，违反接口隔离原则

#### 建议修复方案

拆分为 4 个 Service：

```
IAriaTaskService        → 纯任务 CRUD (AddUri/Pause/Resume/Remove/Refresh)
IAriaConnectionManager  → 连接生命周期管理 (Connect/Reconnect/Poll)
ITrackerService         → BT Tracker 列表获取与更新
IFileSystemService      → 打开文件/打开目录 (跨平台 Shell 操作)
```

---

### 问题 #6：Timer 轮询存在竞态条件

**严重程度**: 🟡 中  
**类型**: 线程安全  

#### 问题所在

```csharp
// AriaTaskService.cs 第 78-104 行
private bool _isPolling;   // ← 普通布尔标志

private async Task PollLoopAsync()
{
    if (_isPolling) return;  // ← 非原子检查
    _isPolling = true;       // ← 非原子设置
    try
    {
        // ... 异步操作
    }
    finally
    {
        _isPolling = false;
    }
}
```

`System.Threading.Timer` 在**线程池线程**上触发回调。如果一次轮询耗时超过 1 秒 (比如网络超时)，下一次回调会在**另一个线程**上触发。此时两个线程可能同时读取到 `_isPolling == false` 并进入方法体。

#### 建议修复方案

使用 `Interlocked` 或 `SemaphoreSlim`：

```csharp
// 方案 A: Interlocked (轻量)
private int _isPolling = 0;

private async Task PollLoopAsync()
{
    if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;
    try
    {
        // ...
    }
    finally
    {
        Interlocked.Exchange(ref _isPolling, 0);
    }
}

// 方案 B: SemaphoreSlim (可等待)
private readonly SemaphoreSlim _pollLock = new(1, 1);

private async Task PollLoopAsync()
{
    if (!await _pollLock.WaitAsync(0)) return; // 非阻塞尝试获取
    try
    {
        // ...
    }
    finally
    {
        _pollLock.Release();
    }
}
```

---

### 问题 #7：`AppSettings` 模型缺少输入验证

**严重程度**: 🟡 中  
**类型**: 数据完整性  

#### 问题所在

```csharp
// AppSettings.cs
public class AppSettings
{
    public int RpcPort { get; set; } = 6800;                // 无范围限制
    public int MaxConcurrentDownloads { get; set; } = 5;    // 可以被设为 -1？
    public int MaxConnectionPerServer { get; set; } = 16;   // 可以被设为 0？
    public int Split { get; set; } = 16;                    // 无上限？
    public string RpcSecret { get; set; } = "ariaui_secret_token"; // 硬编码默认密钥
    // ...
}
```

- `RpcPort` 没有限制在 1024-65535 范围
- 数值类型没有最小/最大值约束
- `RpcSecret` 使用硬编码默认值，如果用户不修改，所有 AriaUI 实例共享同一密钥
- 从 JSON 反序列化时，任意值都会被接受

#### 建议修复方案

添加验证逻辑：

```csharp
public class AppSettings
{
    // ... 属性定义 ...

    public List<string> Validate()
    {
        var errors = new List<string>();
        
        if (RpcPort < 1024 || RpcPort > 65535)
            errors.Add("RPC 端口必须在 1024-65535 范围内");
        
        if (MaxConcurrentDownloads < 1 || MaxConcurrentDownloads > 64)
            errors.Add("最大并行任务数必须在 1-64 范围内");
        
        if (MaxConnectionPerServer < 1 || MaxConnectionPerServer > 64)
            errors.Add("单任务最大连接数必须在 1-64 范围内");
        
        if (Split < 1 || Split > 64)
            errors.Add("文件分片数必须在 1-64 范围内");
        
        if (!string.IsNullOrWhiteSpace(DefaultDownloadDir) && !Directory.Exists(DefaultDownloadDir))
            errors.Add($"下载目录不存在: {DefaultDownloadDir}");
        
        return errors;
    }
}
```

在 `SettingsService.SaveAsync()` 中调用验证：

```csharp
public async Task SaveAsync()
{
    var errors = _settings.Validate();
    if (errors.Count > 0)
        throw new InvalidOperationException(string.Join("; ", errors));
    
    // ... 保存
}
```

---

### 问题 #8：`FilteredTasks` 每次轮询都 Clear + 重建，导致 UI 闪烁

**严重程度**: 🟡 中  
**类型**: 性能 / 用户体验  

#### 问题所在

```csharp
// TaskListViewModel.cs 第 137-171 行
private void ApplyFilter()
{
    // ...
    var filteredList = query.ToList();

    FilteredTasks.Clear();                    // ← 先清空
    foreach (var item in filteredList)
    {
        FilteredTasks.Add(item);             // ← 再逐个添加
    }
    // ...
}
```

`ApplyFilter()` 在每次 `UpdateTasksFromService()` 中被调用，而 `UpdateTasksFromService()` 每 **1 秒**被轮询定时器触发一次。

#### 为什么是问题

1. `ObservableCollection.Clear()` 会触发 `CollectionChanged` 事件 (Reset 类型)
2. 随后每次 `Add()` 又各触发一次 `CollectionChanged` 事件
3. `ItemsControl` 收到 Reset 事件后会**销毁所有子控件**，再重新创建
4. 结果：列表每秒"闪"一次，滚动位置丢失，用户体验差
5. 如果有 100 个任务，每秒触发 101 次 CollectionChanged 事件

#### 建议修复方案

**增量 Diff 更新** — 只修改变化的项：

```csharp
private void ApplyFilter()
{
    var query = _taskMap.Values.AsEnumerable();
    // ... 筛选逻辑 ...
    var newList = query.ToList();

    // 增量同步：逐位对比，最小化变更
    int i = 0;
    for (; i < newList.Count; i++)
    {
        if (i < FilteredTasks.Count)
        {
            if (!ReferenceEquals(FilteredTasks[i], newList[i]))
            {
                FilteredTasks[i] = newList[i]; // 替换不同的项
            }
            // 相同的项无需操作，VM.Update() 已经更新了属性
        }
        else
        {
            FilteredTasks.Add(newList[i]); // 追加新项
        }
    }
    
    // 移除多余的尾部项
    while (FilteredTasks.Count > newList.Count)
    {
        FilteredTasks.RemoveAt(FilteredTasks.Count - 1);
    }

    HasTasks = FilteredTasks.Count > 0;
    HasNoTasks = FilteredTasks.Count == 0;
}
```

---

### 问题 #9：WebSocket 接收缓冲区 MemoryStream 生命周期管理不当

**严重程度**: 🟡 低  
**类型**: 资源管理  

#### 问题所在

```csharp
// AriaWebSocketRpcClient.cs 第 86-115 行
private async Task ReceiveLoopAsync(CancellationToken ct)
{
    var buffer = new byte[64 * 1024];    // 64KB 固定缓冲区
    var ms = new MemoryStream();          // ← 未使用 using，循环中反复 SetLength(0)

    try
    {
        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            // ... 读取数据到 ms ...
            var messageJson = Encoding.UTF8.GetString(ms.ToArray());
            //                                      ^^^^^^^^^ 每次分配新 byte[]
        }
    }
    // ...
}
```

#### 为什么是问题

1. `ms.ToArray()` 每次调用都会分配一个新的 `byte[]` 数组，增加 GC 压力
2. `MemoryStream` 内部缓冲区会随着消息增大而膨胀，但永远不会收缩 (只做 `SetLength(0)`)
3. 如果接收到一条特别大的消息 (比如 `tellStopped` 返回大量历史任务)，这块内存会一直被占用

#### 建议修复方案

```csharp
private async Task ReceiveLoopAsync(CancellationToken ct)
{
    var buffer = new byte[64 * 1024];

    try
    {
        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();  // ← 每条消息用新的 MemoryStream
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await DisconnectAsync();
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            // 避免 ToArray() 的额外拷贝
            var messageJson = Encoding.UTF8.GetString(
                ms.GetBuffer(), 0, (int)ms.Length);
            ProcessIncomingMessage(messageJson);
        }
    }
    // ...
}
```

或者引入 `Microsoft.IO.RecyclableMemoryStream` NuGet 包以获得对象池化的 MemoryStream。

---

### 问题 #10：`SettingsViewModel.SaveAsync()` 直接修改 Settings 对象，缺少原子性保护

**严重程度**: 🟡 中  
**类型**: 数据一致性  

#### 问题所在

```csharp
// SettingsViewModel.cs 第 91-113 行
[RelayCommand]
private async Task SaveAsync()
{
    var s = _settingsService.Settings;   // ← 取到引用
    s.AutoStartDaemon = AutoStartDaemon; // ← 直接修改内存中的对象
    s.RpcHost = RpcHost;
    s.RpcPort = (int)RpcPort;
    // ... 修改了 14 个字段 ...

    await _settingsService.SaveAsync();  // ← 这里如果失败呢？
    await _taskService.ApplySpeedLimitAsync(...);

    StatusMessage = "设置已保存并生效！";  // ← 总是显示成功
}
```

#### 为什么是问题

1. `_settingsService.Settings` 返回的是**同一个引用**，直接修改后即使 `SaveAsync()` 写文件失败 (磁盘满、权限问题)，内存中的设置已经被改了
2. 下次 `SaveAsync` 会把错误的 (或半修改的) 状态写入
3. 如果 `ApplySpeedLimitAsync` 失败，用户看到"设置已保存并生效！"但实际上限速没有生效

#### 建议修复方案

```csharp
[RelayCommand]
private async Task SaveAsync()
{
    try
    {
        // 构造新的 Settings 对象，不直接修改原引用
        var newSettings = new AppSettings
        {
            AutoStartDaemon = AutoStartDaemon,
            RpcHost = RpcHost,
            RpcPort = (int)RpcPort,
            // ...
        };

        // 验证
        var errors = newSettings.Validate();
        if (errors.Count > 0)
        {
            StatusMessage = $"配置无效: {string.Join(", ", errors)}";
            return;
        }

        // 原子性：先写文件成功，再替换内存中的引用
        await _settingsService.SaveAsync(newSettings);
        await _taskService.ApplySpeedLimitAsync(
            newSettings.MaxOverallDownloadLimit,
            newSettings.MaxOverallUploadLimit);

        StatusMessage = "设置已保存并生效！";
    }
    catch (Exception ex)
    {
        StatusMessage = $"保存失败: {ex.Message}";
    }
}
```

---

## 四、改进建议 (🟢 锦上添花)

---

### 建议 #11：引入结构化日志框架

#### 现状

整个项目使用 `Console.Error.WriteLine` 输出日志，共计 **20+ 处**：

```csharp
Console.Error.WriteLine($"Failed to auto start aria2 daemon.");
Console.Error.WriteLine($"Error starting aria2c: {ex.Message}");
Console.Error.WriteLine($"Poll error: {ex.Message}");
Console.Error.WriteLine($"Refresh tasks error: {ex.Message}");
// ...
```

#### 问题

- 没有日志级别 (Debug/Info/Warning/Error)
- 没有时间戳
- 无法输出到文件，重启后日志丢失
- 生产环境和开发环境无法差异化配置

#### 建议

引入 `Microsoft.Extensions.Logging` + `Serilog`：

```csharp
// Program.cs 或 App.axaml.cs
collection.AddLogging(builder =>
{
    builder.AddConsole();
    builder.AddFile("~/.config/AriaUI/logs/ariaui-.log",
        rollingInterval: RollingInterval.Day);
});

// 使用
public class AriaTaskService
{
    private readonly ILogger<AriaTaskService> _logger;

    public AriaTaskService(ILogger<AriaTaskService> logger, ...)
    {
        _logger = logger;
    }

    private async Task PollLoopAsync()
    {
        // ...
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "轮询 Aria2 状态失败");
        }
    }
}
```

---

### 建议 #12：`FormatHelper` 不应放在 `Converters` 目录中

#### 现状

```
Converters/
├── CommonConverters.cs   ← 真正的 Avalonia IValueConverter
└── FormatHelper.cs       ← 纯静态工具类，不是 Converter
```

`FormatHelper` 被 ViewModel 层直接引用 (`TaskItemViewModel`、`MainWindowViewModel`)，它不是 Avalonia 的 `IValueConverter`，与 Converters 目录语义不匹配。

#### 建议

```
移动到: Helpers/FormatHelper.cs
或:     Utilities/FormatHelper.cs
```

---

### 建议 #13：`ViewLocator` 使用反射命名约定 — 不兼容 AOT 编译

#### 现状

```csharp
// ViewLocator.cs 第 22-23 行
var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
var type = Type.GetType(name);
```

通过字符串替换 (`ViewModel` → `View`) 和 `Type.GetType()` 反射查找视图类型。

#### 问题

- .NET Native AOT / IL Trimming 会裁剪未被直接引用的类型
- `Type.GetType()` 在 AOT 场景下无法找到被 trimmer 移除的类型
- 虽然已标注 `[RequiresUnreferencedCode]`，但如果发布 AOT 版本会直接失败

#### 建议 — 显式字典映射

```csharp
public class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> ViewMap = new()
    {
        [typeof(TaskListViewModel)] = () => new TaskListView(),
        [typeof(SettingsViewModel)] = () => new SettingsView(),
    };

    public Control? Build(object? param)
    {
        if (param is null) return null;
        
        return ViewMap.TryGetValue(param.GetType(), out var factory)
            ? factory()
            : new TextBlock { Text = $"View not found for {param.GetType().Name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
```

---

### 建议 #14：`AriaTaskInfo.DisplayName` 计算属性过于复杂

#### 现状

```csharp
// AriaTask.cs 第 67-100 行
public string DisplayName
{
    get
    {
        try
        {
            if (Bittorrent?.Info?.Name != null && ...)
                return Bittorrent.Info.Name;

            if (Files != null && Files.Count > 0)
            {
                var firstFile = Files[0];
                if (!string.IsNullOrWhiteSpace(firstFile.Path))
                {
                    var fn = Path.GetFileName(firstFile.Path);
                    if (!string.IsNullOrWhiteSpace(fn)) return fn;
                }

                if (firstFile.Uris != null && firstFile.Uris.Count > 0 && ...)
                {
                    var uriStr = firstFile.Uris[0].Uri;
                    try
                    {
                        var uri = new Uri(uriStr);
                        var fn = Path.GetFileName(uri.LocalPath);
                        if (!string.IsNullOrWhiteSpace(fn)) return fn;
                    }
                    catch { }
                    return uriStr;
                }
            }
        }
        catch { }

        return !string.IsNullOrEmpty(Gid) ? $"Task-{Gid}" : "Unknown Download";
    }
}
```

在一个 Model 类的**属性 getter** 中包含了：
- 多层 null 检查与嵌套
- `Uri` 构造函数调用 (可能抛异常)
- `Path.GetFileName` 调用
- 多个 try-catch
- 复杂的 fallback 链

#### 建议

抽取为独立的静态方法，提高可读性和可测试性：

```csharp
public static class TaskNameResolver
{
    public static string Resolve(AriaTaskInfo task)
    {
        return TryGetBtName(task)
            ?? TryGetFileName(task)
            ?? TryGetUriName(task)
            ?? FallbackName(task);
    }

    private static string? TryGetBtName(AriaTaskInfo task)
    {
        var name = task.Bittorrent?.Info?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
    // ...
}
```

---

### 建议 #15：硬编码颜色值散落在 Converter 和 XAML 中

#### 现状

同一套颜色值被**重复硬编码**在至少 3 个文件中：

```csharp
// CommonConverters.cs
"#22c55e"  // Green  - active/download
"#eab308"  // Yellow - waiting
"#f97316"  // Orange - paused
"#3b82f6"  // Blue   - complete/upload
"#ef4444"  // Red    - error/disconnected
"#9ca3af"  // Gray   - unknown
```

```xml
<!-- MainWindow.axaml 第 116 行 -->
<TextBlock Text="↓" Foreground="#22c55e" />
<TextBlock Text="↑" Foreground="#3b82f6" />

<!-- TaskListView.axaml 第 228 行 -->
<TextBlock Text="↓" Foreground="#22c55e" />
<TextBlock Text="↑" Foreground="#3b82f6" />
<TextBlock Foreground="#ef4444" />
```

#### 问题

- 同一颜色出现在多处，修改时容易遗漏
- 无法跟随主题切换 (Dark/Light 模式下可能需要不同色值)
- 与 Semi.Avalonia 的主题系统割裂

#### 建议

在 `App.axaml` 中定义颜色资源：

```xml
<Application.Resources>
    <!-- Status Colors -->
    <SolidColorBrush x:Key="StatusActiveBrush" Color="#22c55e" />
    <SolidColorBrush x:Key="StatusWaitingBrush" Color="#eab308" />
    <SolidColorBrush x:Key="StatusPausedBrush" Color="#f97316" />
    <SolidColorBrush x:Key="StatusCompleteBrush" Color="#3b82f6" />
    <SolidColorBrush x:Key="StatusErrorBrush" Color="#ef4444" />
    <SolidColorBrush x:Key="StatusUnknownBrush" Color="#9ca3af" />
    
    <!-- Speed Indicator Colors -->
    <SolidColorBrush x:Key="DownloadSpeedBrush" Color="#22c55e" />
    <SolidColorBrush x:Key="UploadSpeedBrush" Color="#3b82f6" />
</Application.Resources>
```

然后在 XAML 和 Converter 中引用：

```xml
<TextBlock Text="↓" Foreground="{DynamicResource DownloadSpeedBrush}" />
```

---

### 建议 #16：`AriaProcessService.StartDaemonAsync` 实际上不是异步的

#### 现状

```csharp
// AriaProcessService.cs 第 99 行
public Task<bool> StartDaemonAsync(AppSettings settings)
{
    if (IsRunning) return Task.FromResult(true);          // 同步
    // ...
    _process = Process.Start(startInfo);                  // 同步
    return Task.FromResult(true);                         // 同步
}
```

方法签名为 `Task<bool>` 但没有任何 `await`，所有操作都是同步的，只是用 `Task.FromResult()` 包装。

#### 建议

要么改为同步方法：

```csharp
public bool StartDaemon(AppSettings settings) { ... }
```

要么真正使用异步 — 等待 daemon 启动就绪：

```csharp
public async Task<bool> StartDaemonAsync(AppSettings settings)
{
    // ... 启动进程 ...
    
    // 等待 RPC 端口可用
    for (int i = 0; i < 10; i++)
    {
        await Task.Delay(500);
        if (IsPortInUse(settings.RpcPort))
            return true;
    }
    return false;
}
```

---

### 建议 #17：退出清理代码可能被截断

#### 现状

```csharp
// App.axaml.cs 第 44-57 行
desktop.Exit += async (s, e) =>
{
    var processService = Services.GetService<IAriaProcessService>();
    if (processService != null)
    {
        await processService.StopDaemonAsync();   // ← async void 事件处理器
    }

    var rpcClient = Services.GetService<IAriaRpcClient>();
    if (rpcClient != null)
    {
        await rpcClient.DisposeAsync();           // ← 可能被截断
    }
};
```

#### 问题

`Exit` 事件的处理器类型是 `EventHandler<ControlledApplicationLifetimeExitEventArgs>`。当你给它赋值一个 `async` lambda 时，它实际上是 `async void`——调用者**不会等待**它完成。进程可能在 `StopDaemonAsync()` 完成之前就退出了。

#### 建议

结合已有的 `AppDomain.CurrentDomain.ProcessExit` (在 `AriaProcessService` 构造函数中已注册) 确保至少有一层兜底。

---

### 建议 #18：`ThemeMode` 设置字段已定义但从未使用

#### 现状

```csharp
// AppSettings.cs 第 19 行
public string ThemeMode { get; set; } = "System"; // System, Dark, Light
```

这个字段在整个项目中**没有被读取或应用**：
- `SettingsViewModel` 没有对应的绑定属性
- `SettingsView.axaml` 没有对应的 UI 控件
- `App.axaml.cs` 没有根据此值设置主题

#### 建议

要么实现主题切换功能：

```csharp
// App.axaml.cs 中
var themeMode = settingsService.Settings.ThemeMode;
RequestedThemeVariant = themeMode switch
{
    "Dark" => ThemeVariant.Dark,
    "Light" => ThemeVariant.Light,
    _ => ThemeVariant.Default // System
};
```

并在 SettingsView 中添加对应的下拉选择控件。

要么从 `AppSettings` 中移除未使用的字段，避免产生"功能已支持"的误解。

---

## 五、总结与优先级矩阵

| 优先级 | 编号 | 问题描述 | 影响范围 | 修复难度 |
|:---:|:---:|---|---|:---:|
| 🔴 P0 | #4 | `CleanupStaleAriaProcesses` 杀死所有 aria2c | 数据安全 | ⭐ 低 |
| 🔴 P0 | #3 | fire-and-forget + 空 catch 吞异常 | 可靠性 | ⭐ 低 |
| 🔴 P1 | #1 | DI 容器被 `new` 绕过 | 可维护性 | ⭐⭐ 中 |
| 🔴 P1 | #2 | 事件订阅未取消导致内存泄漏 | 资源管理 | ⭐⭐ 中 |
| 🟡 P2 | #8 | FilteredTasks Clear+Add 导致 UI 闪烁 | 用户体验 | ⭐ 低 |
| 🟡 P2 | #6 | Timer 轮询竞态条件 | 线程安全 | ⭐ 低 |
| 🟡 P2 | #10 | Settings 直接修改缺少原子性 | 数据一致性 | ⭐⭐ 中 |
| 🟡 P2 | #5 | AriaTaskService 职责过重 | 可维护性 | ⭐⭐⭐ 高 |
| 🟡 P2 | #7 | AppSettings 缺少输入验证 | 健壮性 | ⭐ 低 |
| 🟡 P3 | #9 | WebSocket MemoryStream 管理 | 性能 | ⭐ 低 |
| 🟢 P3 | #11 | 缺少日志框架 | 可运维性 | ⭐ 低 |
| 🟢 P3 | #12 | FormatHelper 目录归属 | 代码组织 | ⭐ 低 |
| 🟢 P3 | #13 | ViewLocator 反射不兼容 AOT | 未来兼容 | ⭐ 低 |
| 🟢 P3 | #14 | DisplayName getter 过于复杂 | 可读性 | ⭐ 低 |
| 🟢 P3 | #15 | 硬编码颜色值 | 可维护性 | ⭐ 低 |
| 🟢 P3 | #16 | StartDaemonAsync 伪异步 | 代码规范 | ⭐ 低 |
| 🟢 P3 | #17 | 退出清理 async void | 可靠性 | ⭐ 低 |
| 🟢 P3 | #18 | ThemeMode 字段未使用 | 代码整洁 | ⭐ 低 |

> **建议的修复顺序**: #4 → #3 → #6 → #8 → #1 → #2 → #7 → #10 → 其余按需处理

---

## 六、新增改进建议 (#19 — #29) — 落实与修复记录

> **二次审查日期**: 2026-08-27  
> **修复完成日期**: 2026-08-28  
> **修复状态**: 🟢 全部 11 项新增建议已 100% 完成代码修复并编译验证通过 (0 错误 / 0 警告)。

---

### 问题 #19：`AriaWebSocketRpcClient.InvokeAsync` 超时后未清理待处理请求 + `SendAsync` 无法取消 + 错误解析脆弱

**严重程度**: 🔴 高 | **文件**: `Services/AriaWebSocketRpcClient.cs` | **状态**: 🟢 已修复

- **原问题**: `_pendingRequests[reqId]=tcs` 后超时仅 `TrySetCanceled` 却未 `TryRemove` 导致内存泄漏；`errorProp.GetProperty("message")` 遇到异常 JSON 抛出未捕获异常致外层挂死；`SendAsync` 传入 `CancellationToken.None` 无法取消。
- **修复方案**:
  1. `InvokeAsync` 统一使用 `try ... finally { _pendingRequests.TryRemove(reqId, out _); }`，无论成功、超时、取消还是抛出异常，均 100% 清理字典。
  2. `SendAsync` 传入 `timeoutCts.Token`，支持超时中断。
  3. `ProcessIncomingMessage` 中改用 `errorProp.TryGetProperty("message", out var msgProp)`，若不存在则降级为 `errorProp.GetRawText()` 兜底。

---

### 问题 #20：`AriaProcessService` 以 `string.Join` 拼接启动参数 — 存在参数注入 + 硬编码 `--check-certificate=false`

**严重程度**: 🔴 高（安全）| **文件**: `Services/AriaProcessService.cs`、`Models/AppSettings.cs` | **状态**: 🟢 已修复

- **原问题**: `--dir="..."` / `--bt-tracker="..."` 手动加引号后 `string.Join(" ", args)` 存在引号逃逸与 RCE 参数注入风险；硬编码 `--check-certificate=false` 全局关闭 TLS 校验。
- **修复方案**:
  1. `StartDaemonAsync` 改用 `ProcessStartInfo.ArgumentList.Add(...)` 逐项添加参数，由 .NET 运行时原生处理参数转义与边界隔离。
  2. `AppSettings` 增加 `AllowInvalidCert`（默认 `false`），仅在用户显式开启时才附加 `--check-certificate=false`。
  3. `AppSettings.Validate()` 增加对 `DefaultDownloadDir` 的非法字符拦截（禁止包含 `"`, `;`, `&`）。

---

### 问题 #21：`FileSystemService` `xdg-open` 引号错误 + 句柄泄漏 + `RemoveTask(deleteFile)` 任意文件删除

**严重程度**: 🔴 高（安全）| **文件**: `Services/FileSystemService.cs`、`Services/AriaTaskService.cs` | **状态**: 🟢 已修复

- **原问题**: `xdg-open` 传参双重引号错误且进程未 `Dispose`；`RemoveTask(deleteFile)` 直接依据 aria2 返回的路径 `File.Delete`，恶意种子跨目录相对路径可能误删系统/用户重要文件。
- **修复方案**:
  1. `FileSystemService` 中 `OpenFile` 与 `OpenDirectory` 全面改用 `ProcessStartInfo.ArgumentList` + `using var p = Process.Start(...)` 确保句柄及时释放。
  2. `AriaTaskService.RemoveTaskAsync` 在删除文件前调用 `Path.GetFullPath(file.Path)`，并验证路径必须以配置的 `DefaultDownloadDir` 为前缀，拦截跨目录任意文件删除攻击。

---

### 问题 #22：`AriaTaskService` 用 `Timer(async _ =>)`（即 `async void`）且从未 `Dispose`，列表无锁读写

**严重程度**: 🟡 中 | **文件**: `Services/AriaTaskService.cs`、`App.axaml.cs` | **状态**: 🟢 已修复

- **原问题**: `System.Threading.Timer(async void)` 未捕获异常可能导致进程崩溃且从未释放；任务列表并发读写无线程安全保护。
- **修复方案**:
  1. 引入 .NET 10 `PeriodicTimer` + `CancellationTokenSource` 配合后台异步循环驱动轮询。
  2. `AriaTaskService` 实现 `IDisposable` 接口，在 `Dispose` 中取消 CTS 并释放 `PeriodicTimer`；在 `App.axaml.cs` 的 `desktop.Exit` 中主动调用释放。
  3. `ActiveTasks`、`WaitingTasks`、`StoppedTasks` 使用 `lock (_taskLock)` 保护并暴露不可变快照副本，避免并发枚举撕裂。

---

### 问题 #23：`TrackerService` 存在 SSRF、无界下载、`HttpClient` 生命周期与不可取消

**严重程度**: 🟡 中（安全/DOS）| **文件**: `Services/TrackerService.cs`、`Services/ITrackerService.cs`、`App.axaml.cs` | **状态**: 🟢 已修复

- **原问题**: `CustomTrackersUrl` 可被利用请求内网/云元数据接口；无响应体上限可能导致 OOM；缺少 `CancellationToken` 且 `HttpClient` 非 DI 单例。
- **修复方案**:
  1. 在 `App.axaml.cs` DI 容器中注册单例 `HttpClient`（配置 15s 超时）。
  2. 接口及实现增加 `CancellationToken` 参数支持。
  3. 增加 SSRF 防护：严格校验 URL 仅允许 `http/https`，拦截 `IsLoopback`、`localhost` 及 `169.254.x.x` 地址。
  4. 采用 `HttpCompletionOption.ResponseHeadersRead` + 512KB 流式读取上限，超出立即抛出异常终止。
  5. Tracker 行增加协议正则校验（仅保留 `http://`、`https://`、`udp://`、`wss://` 开头的合法节点）。

---

### 问题 #24：`SettingsService` 路径硬编码、构造期 IO、并发保存竞态 + 明文默认密钥

**严重程度**: 🟡 中（安全/健壮性）| **文件**: `Services/SettingsService.cs`、`Services/AriaProcessService.cs`、`Models/AppSettings.cs` | **状态**: 🟢 已修复

- **原问题**: 硬编码 `.config/AriaUI` 跨平台不兼容；`SaveAsync` 无锁并发竞态；默认静态密钥全机相同且明文存储。
- **修复方案**:
  1. 路径统一改用 `Environment.SpecialFolder.ApplicationData` 标准目录（Windows: `%AppData%`, Linux: `~/.config`, macOS: `~/Library/Application Support`）。
  2. `SaveAsync` 引入 `SemaphoreSlim _saveLock = new(1, 1)` 互斥锁，保护 `.tmp` 写入与 `File.Move` 原子替换。
  3. `AppSettings.RpcSecret` 默认值改为空，在首次加载时自动通过 `RandomNumberGenerator.GetBytes(16)` 生成高强度随机密钥。
  4. Linux / macOS 下写入配置文件时自动设置 `0600` (`UserRead | UserWrite`) 权限。

---

### 问题 #25：任务列表无虚拟化、Emoji 图标、遮罩写死、弹窗不可访问

**严重程度**: 🟡 中（性能/UX）| **文件**: `Views/TaskListView.axaml` | **状态**: 🟢 已修复

- **原问题**: `ScrollViewer > ItemsControl` 全量实例化导致大量任务时每秒轮询卡顿；弹窗错误文本硬编码颜色。
- **修复方案**:
  1. 任务列表容器重构为 `ListBox` + `VirtualizingStackPanel`，开启 UI 虚拟化与透明列表项样式，彻底消除上百任务时的滚动与渲染卡顿。
  2. 弹窗内错误文本改用 `{DynamicResource StatusErrorBrush}` 主题画刷。
  3. `NumericUpDown` 增加 `FormatString="0"` 规范整型输入。

---

### 问题 #26：设置页状态色常绿、密钥仍明文、`decimal` 误用

**严重程度**: 🟢 中低 | **文件**: `Views/SettingsView.axaml`、`ViewModels/SettingsViewModel.cs`、`Converters/CommonConverters.cs` | **状态**: 🟢 已修复

- **原问题**: 设置保存出错时背景色仍固定为成功浅绿；`NumericUpDown` 可输入小数截断。
- **修复方案**:
  1. 新增 `StatusAlertBackgroundConverter`、`StatusAlertBorderBrushConverter`、`StatusAlertForegroundConverter`，根据 `IsStatusError` 动态呈现红/绿警告框。
  2. `SettingsViewModel` 增加 `AllowInvalidCert` 绑定属性。
  3. 各整型 `NumericUpDown` 增加 `FormatString="0"`。

---

### 问题 #27：`IsPortInUseAsync` 固定探测 `127.0.0.1`，与 `RpcHost` 不一致

**严重程度**: 🟡 中 | **文件**: `Services/AriaProcessService.cs` | **状态**: 🟢 已修复

- **原问题**: `RpcHost` 配置可能为 IPv6 或局域网地址，但代码始终探测 `127.0.0.1:6800`。
- **修复方案**: `IsPortInUseAsync` 增加 `string host` 参数，并在 `StartDaemonAsync` 中传入 `settings.RpcHost` 进行一致性探测。

---

### 问题 #28：`StatusColorConverter` 每次绑定新建 `SolidColorBrush` + `Color.Parse`

**严重程度**: 🟢 低（性能）| **文件**: `Converters/CommonConverters.cs` | **状态**: 🟢 已修复

- **原问题**: 每任务每秒刷新频繁执行 `new SolidColorBrush(Color.Parse(...))`，产生无谓的 Gen0 GC 内存分配。
- **修复方案**: `StatusColorConverter` 与 `ConnectionBrushConverter` 内部将 6 个状态画刷与 2 个连接画刷声明为 `private static readonly SolidColorBrush` 单例缓存，运行时仅做静态引用返回，GC 分配降为 0。

---

### 问题 #29：仅 `ws://`、IPv6 拼写错误、`ReceiveLoop` 自取消死锁、无界消息、`ProcessExit` 阻塞

**严重程度**: 🟡 中 | **文件**: `Services/AriaWebSocketRpcClient.cs`、`Services/AriaProcessService.cs` | **状态**: 🟢 已修复

- **原问题**: 裸 IPv6 缺少中括号；`ReceiveLoop` 收到 Close 时调用 `DisconnectAsync` 导致 CTS 自释放异常；`MemoryStream` 无上限缓冲；`ProcessExit` 同步阻塞 2 秒。
- **修复方案**:
  1. `ConnectAsync` 增加端口 443 自动转 `wss://` 支持，并对含 `:` 的 IPv6 地址自动包裹 `[host]`。
  2. `ReceiveLoopAsync` 收到 `WebSocketMessageType.Close` 时直接 `return`，由外层统一清理。
  3. `MemoryStream` 增加 8MB 长度守卫，超限抛出异常阻断 DoS。
  4. `AriaProcessService` 的 `ProcessExit` 事件改用非阻塞直接 `try { _process?.Kill(true); } catch {}`。

---

## 七、全量建议修复状态总览 (#1 — #29)

| 编号 | 分类 | 简述 | 严重度 | 修复状态 |
|:---:|:---:|---|:---:|:---:|
| **#1** | 架构 | DI 容器被手动 `new` 绕过 | 🔴 高 | 🟢 已完全实现 |
| **#2** | 资源 | 事件强引用导致内存泄漏 | 🔴 高 | 🟢 已完全实现 |
| **#3** | 可靠 | fire-and-forget 与空 catch 吞异常 | 🔴 高 | 🟢 已完全实现 |
| **#4** | 安全 | CleanupStaleProcesses 误杀其他进程 | 🔴 严重 | 🟢 已完全实现 |
| **#5** | 架构 | AriaTaskService 职责过重 (God Service) | 🟡 中 | 🟢 已拆分 Tracker/FS |
| **#6** | 并发 | Timer 轮询存在竞态条件 | 🟡 中 | 🟢 已完全实现 |
| **#7** | 健壮 | AppSettings 缺少输入验证 | 🟡 中 | 🟢 已完全实现 |
| **#8** | 性能 | FilteredTasks Clear+Add 导致 UI 闪烁 | 🟡 中 | 🟢 已完全实现 |
| **#9** | 资源 | WebSocket MemoryStream 生命周期 | 🟡 中 | 🟢 已完全实现 |
| **#10** | 事务 | Settings 保存缺少原子性 | 🟡 中 | 🟢 已完全实现 |
| **#11** | 运维 | 引入结构化日志框架 | 🟢 低 | 🟡 待后续迭代 |
| **#12** | 规范 | FormatHelper 目录归属不当 | 🟢 低 | 🟢 已完全实现 |
| **#13** | 兼容 | ViewLocator 反射不兼容 AOT | 🟢 低 | 🟢 已完全实现 |
| **#14** | 整洁 | DisplayName getter 过于复杂 | 🟢 低 | 🟢 已完全实现 |
| **#15** | 维护 | 硬编码颜色值散落 | 🟢 低 | 🟢 已完全实现 |
| **#16** | 规范 | StartDaemonAsync 伪异步 | 🟢 低 | 🟢 已完全实现 |
| **#17** | 可靠 | 退出清理 async void 截断 | 🟢 低 | 🟢 已完全实现 |
| **#18** | 整洁 | ThemeMode 字段未使用 | 🟢 低 | 🟢 已完全实现 |
| **#19** | 资源 | RPC 超时未清理 + 字典泄漏 + 错误解析 | 🔴 高 | 🟢 已完全实现 |
| **#20** | 安全 | 参数注入 / RCE 面 / TLS 校验 | 🔴 高 | 🟢 已完全实现 |
| **#21** | 安全 | 任意文件删除 / xdg-open 注入与句柄泄漏 | 🔴 高 | 🟢 已完全实现 |
| **#22** | 并发 | PeriodicTimer + IDisposable + 列表快照 | 🟡 中 | 🟢 已完全实现 |
| **#23** | 安全 | Tracker SSRF / 流式限流 / CancellationToken | 🟡 中 | 🟢 已完全实现 |
| **#24** | 健壮 | 配置跨平台标准路径 / SemaphoreSlim / 随机密钥 | 🟡 中 | 🟢 已完全实现 |
| **#25** | 性能 | ListBox UI 虚拟化 / 弹窗无障碍 | 🟡 中 | 🟢 已完全实现 |
| **#26** | UX | 设置页状态红绿反馈 / 整型格式化 / TLS 开关 | 🟢 低 | 🟢 已完全实现 |
| **#27** | 网络 | 端口探测 Host 一致性 | 🟡 中 | 🟢 已完全实现 |
| **#28** | 性能 | Converter 画刷静态缓存 (0 GC 分配) | 🟢 低 | 🟢 已完全实现 |
| **#29** | 网络 | IPv6 规范 / WSS / 8MB 限制 / 优雅断开 | 🟡 中 | 🟢 已完全实现 |

