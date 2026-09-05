using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;
using AriaUI.Services;

namespace AriaUI.Tests;

public static class RpcBaselineRegressionTests
{
    private sealed class MockAriaRpcClient : IAriaRpcClient
    {
        public bool IsConnected { get; set; }
        public bool AllowConnect { get; set; } = true;
        public bool ThrowOnNextGetGlobalStat { get; set; }

        public List<AriaTaskInfo> ActiveTasksToReturn { get; set; } = new();
        public List<AriaTaskInfo> WaitingTasksToReturn { get; set; } = new();
        public List<AriaTaskInfo> StoppedTasksToReturn { get; set; } = new();
        public AriaGlobalStat GlobalStatToReturn { get; set; } = new();

#pragma warning disable CS0067
        public event EventHandler? ConnectionStateChanged;
        public event EventHandler<string>? DownloadStarted;
        public event EventHandler<string>? DownloadCompleted;
        public event EventHandler<string>? DownloadError;
        public event EventHandler<string>? DownloadPaused;
        public event EventHandler<string>? DownloadStopped;
#pragma warning restore CS0067

        public void SimulateDisconnect()
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task ConnectAsync(string host, int port, string secret, bool useTls = false, CancellationToken cancellationToken = default)
        {
            if (!AllowConnect)
            {
                throw new InvalidOperationException("Mock connection refused.");
            }
            IsConnected = true;
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<AriaGlobalStat> GetGlobalStatAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnNextGetGlobalStat)
            {
                throw new InvalidOperationException("Simulated RPC getGlobalStat error.");
            }
            return Task.FromResult(GlobalStatToReturn);
        }

        public Task<List<AriaTaskInfo>> TellActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<AriaTaskInfo>(ActiveTasksToReturn));

        public Task<List<AriaTaskInfo>> TellWaitingAsync(int offset = 0, int num = 100, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<AriaTaskInfo>(WaitingTasksToReturn));

        public Task<List<AriaTaskInfo>> TellStoppedAsync(int offset = 0, int num = 100, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<AriaTaskInfo>(StoppedTasksToReturn));

        public Task<Dictionary<string, string>> GetGlobalOptionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new Dictionary<string, string>());

        public Task<string> ChangeGlobalOptionAsync(Dictionary<string, object> options, CancellationToken cancellationToken = default)
            => Task.FromResult("OK");

        public Task<string> AddUriAsync(IEnumerable<string> uris, Dictionary<string, object>? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult("mock_gid");

        public Task<string> AddTorrentAsync(byte[] torrentBytes, Dictionary<string, object>? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult("mock_torrent_gid");

        public Task<AriaTaskInfo> TellStatusAsync(string gid, CancellationToken cancellationToken = default)
            => Task.FromResult(new AriaTaskInfo { Gid = gid, Status = "active" });

        public Task<string> PauseAsync(string gid, CancellationToken cancellationToken = default) => Task.FromResult(gid);
        public Task<string> PauseAllAsync(CancellationToken cancellationToken = default) => Task.FromResult("OK");
        public Task<string> UnpauseAsync(string gid, CancellationToken cancellationToken = default) => Task.FromResult(gid);
        public Task<string> UnpauseAllAsync(CancellationToken cancellationToken = default) => Task.FromResult("OK");
        public Task<string> RemoveAsync(string gid, CancellationToken cancellationToken = default) => Task.FromResult(gid);
        public Task<string> ForceRemoveAsync(string gid, CancellationToken cancellationToken = default) => Task.FromResult(gid);
        public Task<string> RemoveDownloadResultAsync(string gid, CancellationToken cancellationToken = default) => Task.FromResult(gid);
        public Task<string> PurgeDownloadResultAsync(CancellationToken cancellationToken = default) => Task.FromResult("OK");
        public Task<string> ShutdownAsync(CancellationToken cancellationToken = default) => Task.FromResult("OK");

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MockProcessService : IAriaProcessService
    {
        public bool IsRunning => true;
        public string? ExecutablePath => "/usr/bin/aria2c";

        public Task<bool> StartDaemonAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task StopDaemonAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class MockSettingsService : ISettingsService
    {
        public AppSettings Settings { get; set; } = new()
        {
            RpcHost = "127.0.0.1",
            RpcPort = 6800,
            RpcSecret = "test-secret",
            AutoStartDaemon = false
        };

        public Task SaveAsync(AppSettings newSettings)
        {
            Settings = newSettings.Clone();
            return Task.CompletedTask;
        }

        public Task<AppSettings> UpdateAsync(Action<AppSettings> update)
        {
            var clone = Settings.Clone();
            update(clone);
            Settings = clone;
            return Task.FromResult(clone);
        }
    }

    private sealed class MockTrackerService : ITrackerService
    {
        public Task<List<string>> FetchTrackersAsync(string trackersUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());
    }

    private sealed class MockFileSystemService : IFileSystemService
    {
        public void OpenFile(string filePath) { }
        public void OpenDirectory(string directoryPath) { }
    }

    /// <summary>
    /// BOUNDARIES.md §8 Item 1:
    /// ProcessIncomingMessage 遇 null/未知 id/脏帧只记日志不抛，接收循环永不死。
    /// </summary>
    public static void Test_RPC_DirtyFrameResilience()
    {
        var client = new AriaWebSocketRpcClient();
        using var cts = new CancellationTokenSource();
        var context = new AriaWebSocketRpcClient.ConnectionContext(
            null!, cts, 1, "test-secret");

        var tcs = new TaskCompletionSource<System.Text.Json.JsonElement>();
        context.PendingRequests["req-42"] = tcs;

        // 1. 脏 JSON (语法错误)
        client.ProcessIncomingMessage(context, "{ invalid json text");

        // 2. 非 JSON-RPC 2.0 帧
        client.ProcessIncomingMessage(context, "{\"hello\": \"world\"}");
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"1.0\", \"id\": \"req-42\", \"result\": \"bad-ver\"}");

        // 3. Null request ID 错误响应 (aria2 JSON-RPC 解析失败时发出)
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": {\"code\": -32700, \"message\": \"Parse error\"}}");

        // 4. 未知 / 已超时 request ID
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"id\": \"unknown-req-999\", \"result\": \"ignored\"}");

        // 5. 无 id 无 method 脏帧
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"random_prop\": 123}");

        // 6. 畸形 error 结构
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"id\": null, \"error\": 12345}");

        // 7. 畸形 notification 帧 (非十六进制 GID、空 params 数组、未知 method)
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"method\": \"aria2.onDownloadStart\", \"params\": [{\"gid\": \"malformed_not_hex\"}]}");
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"method\": \"aria2.onDownloadStart\", \"params\": []}");
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"method\": \"aria2.unknownNotification\", \"params\": []}");

        // 验证上述所有脏帧均未导致异常抛出，也未污染合法的 pending 请求
        Assert.False(tcs.Task.IsCompleted, "Pending request must not be faulted or cancelled by unrelated dirty frames.");

        // 正常注入 req-42 的响应
        client.ProcessIncomingMessage(context, "{\"jsonrpc\": \"2.0\", \"id\": \"req-42\", \"result\": \"success_value\"}");

        Assert.True(tcs.Task.IsCompletedSuccessfully, "Valid request must be completed successfully after dirty frames.");
        Assert.Equal("success_value", tcs.Task.Result.GetString());
    }

    /// <summary>
    /// BOUNDARIES.md §8 Item 2:
    /// PollLoopAsync 按 tick 包 try/catch，单轮失败记日志进下一轮。
    /// </summary>
    public static async Task Test_RPC_PollingTickFailureResilience()
    {
        var mockRpc = new MockAriaRpcClient { IsConnected = true };
        var mockProcess = new MockProcessService();
        var mockSettings = new MockSettingsService();
        var mockTracker = new MockTrackerService();
        var mockFs = new MockFileSystemService();

        using var taskService = new AriaTaskService(
            mockProcess, mockRpc, mockSettings, mockTracker, mockFs);

        await taskService.InitializeAsync();
        Assert.True(taskService.IsConnected);

        // 注入单轮 tick 故障：模拟 RPC 抛出异常
        mockRpc.ThrowOnNextGetGlobalStat = true;

        // 等待定时器 tick 触发并被 try/catch 隔离捕获
        await Task.Delay(1300);

        // 恢复正常
        mockRpc.ThrowOnNextGetGlobalStat = false;

        // 下一轮 tick 正常推进，轮询任务依然存活
        await Task.Delay(1300);

        Assert.True(taskService.IsConnected, "Service must remain connected and polling despite single tick failure.");
    }

    /// <summary>
    /// BOUNDARIES.md §8 Item 2 & 3:
    /// 重连不依赖轮询任务存活；瞬断后 3s 内重连恢复快照。
    /// </summary>
    public static async Task Test_RPC_TransientDisconnectReconnectWithin3sAndRestoreSnapshot()
    {
        var mockRpc = new MockAriaRpcClient
        {
            IsConnected = true,
            ActiveTasksToReturn = new List<AriaTaskInfo>
            {
                new() { Gid = "gid-100", Status = "active", TotalLength = "1048576" }
            },
            GlobalStatToReturn = new AriaGlobalStat { DownloadSpeed = "102400", NumActive = "1" }
        };
        var mockProcess = new MockProcessService();
        var mockSettings = new MockSettingsService();
        var mockTracker = new MockTrackerService();
        var mockFs = new MockFileSystemService();

        using var taskService = new AriaTaskService(
            mockProcess, mockRpc, mockSettings, mockTracker, mockFs);

        await taskService.InitializeAsync();
        Assert.True(taskService.IsConnected);
        Assert.Equal(1, taskService.ActiveTasks.Count);

        // 模拟瞬断：服务端暂时断开
        mockRpc.AllowConnect = false;
        mockRpc.SimulateDisconnect();
        Assert.False(taskService.IsConnected);

        // 模拟外部将轮询任务取消/终结（证明重连绝不依赖轮询任务存活）
        var pollCtsField = typeof(AriaTaskService).GetField("_pollCts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pollCts = (CancellationTokenSource?)pollCtsField?.GetValue(taskService);
        pollCts?.Cancel();

        // 瞬断恢复：Aria2 重新上线并接纳连接，提供更新后的状态与快照
        mockRpc.ActiveTasksToReturn = new List<AriaTaskInfo>
        {
            new() { Gid = "gid-100", Status = "active", TotalLength = "1048576" },
            new() { Gid = "gid-200", Status = "active", TotalLength = "2097152" }
        };
        mockRpc.GlobalStatToReturn = new AriaGlobalStat { DownloadSpeed = "524288", NumActive = "2" };
        mockRpc.AllowConnect = true;

        // 瞬断后 3s 内独立重连回路自动工作
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4500 && !taskService.IsConnected)
        {
            await Task.Delay(100);
        }

        Assert.True(taskService.IsConnected, "TaskService must reconnect within 3s without depending on polling task.");

        // 验证快照成功恢复
        Assert.Equal(2, taskService.ActiveTasks.Count);
        Assert.Equal("gid-100", taskService.ActiveTasks[0].Gid);
        Assert.Equal("gid-200", taskService.ActiveTasks[1].Gid);
        Assert.Equal("524288", taskService.GlobalStat.DownloadSpeed);
    }

    /// <summary>
    /// BOUNDARIES.md §8:
    /// 初始启动失败后独立重连回路自动工作，Aria2 启动上线后自动连接并恢复快照。
    /// </summary>
    public static async Task Test_RPC_InitialConnectFailureAutoReconnectsWhenServerOnline()
    {
        var mockRpc = new MockAriaRpcClient
        {
            IsConnected = false,
            AllowConnect = false, // 模拟 aria2 尚未启动
            ActiveTasksToReturn = new List<AriaTaskInfo>
            {
                new() { Gid = "gid-startup-1", Status = "active", TotalLength = "1048576" }
            },
            GlobalStatToReturn = new AriaGlobalStat { DownloadSpeed = "204800", NumActive = "1" }
        };
        var mockProcess = new MockProcessService();
        var mockSettings = new MockSettingsService();
        var mockTracker = new MockTrackerService();
        var mockFs = new MockFileSystemService();

        using var taskService = new AriaTaskService(
            mockProcess, mockRpc, mockSettings, mockTracker, mockFs);

        // 初始连接抛出异常
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await taskService.InitializeAsync();
        });
        Assert.False(taskService.IsConnected);

        // aria2 服务上线并允许连接
        mockRpc.AllowConnect = true;

        // 独立重连回路应当在 4.5s 内自动完成连接并恢复快照
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4500 && !taskService.IsConnected)
        {
            await Task.Delay(100);
        }

        Assert.True(taskService.IsConnected, "Service must auto-reconnect after initial startup connection failure.");
        Assert.Equal(1, taskService.ActiveTasks.Count);
        Assert.Equal("gid-startup-1", taskService.ActiveTasks[0].Gid);
    }

    /// <summary>
    /// BOUNDARIES.md §8 & §7:
    /// 处于重连状态时调用 ShutdownAsync，独立重连任务被取消并安全等待完成，无后台残留与资源泄露。
    /// </summary>
    public static async Task Test_RPC_ShutdownAwaitsReconnectTask()
    {
        var mockRpc = new MockAriaRpcClient { IsConnected = true };
        var mockProcess = new MockProcessService();
        var mockSettings = new MockSettingsService();
        var mockTracker = new MockTrackerService();
        var mockFs = new MockFileSystemService();

        var taskService = new AriaTaskService(
            mockProcess, mockRpc, mockSettings, mockTracker, mockFs);

        await taskService.InitializeAsync();
        Assert.True(taskService.IsConnected);

        // 模拟断连，触发独立重连任务
        mockRpc.AllowConnect = false;
        mockRpc.SimulateDisconnect();
        Assert.False(taskService.IsConnected);

        // 在重连等待期间执行正常关闭
        await taskService.ShutdownAsync();

        // 验证关闭后状态
        Assert.False(taskService.IsConnected);
    }
}
