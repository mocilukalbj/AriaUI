using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using AriaUI.Models;

namespace AriaUI.Services;

public interface IAriaTaskService : IDisposable
{
    bool IsConnected { get; }
    AriaGlobalStat GlobalStat { get; }
    List<AriaTaskInfo> ActiveTasks { get; }
    List<AriaTaskInfo> WaitingTasks { get; }
    List<AriaTaskInfo> StoppedTasks { get; }
    event EventHandler? TasksUpdated;
    event EventHandler? GlobalStatUpdated;
    event EventHandler<string>? NotificationReceived;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshTasksAsync(CancellationToken cancellationToken = default);
    Task<string> AddUriAsync(string url, string? saveDir = null, int? split = null, string? referer = null, string? userAgent = null, CancellationToken cancellationToken = default);
    Task<string> AddTorrentAsync(string filePath, string? saveDir = null, CancellationToken cancellationToken = default);
    Task PauseTaskAsync(string gid, CancellationToken cancellationToken = default);
    Task ResumeTaskAsync(string gid, CancellationToken cancellationToken = default);
    Task RemoveTaskAsync(string gid, bool deleteFile = false, CancellationToken cancellationToken = default);
    Task PauseAllTasksAsync(CancellationToken cancellationToken = default);
    Task ResumeAllTasksAsync(CancellationToken cancellationToken = default);
    Task PurgeCompletedTasksAsync(CancellationToken cancellationToken = default);
    Task ApplySpeedLimitAsync(long downloadLimitBytes, long uploadLimitBytes, CancellationToken cancellationToken = default);
    Task SaveAndApplySettingsAsync(AppSettings currentSettings, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
    Task UpdateTrackersAsync(CancellationToken cancellationToken = default);
    void OpenFile(string filePath);
    void OpenDirectory(string directoryPath);
}

public sealed class SettingsApplicationException(string message, Exception innerException)
    : Exception(message, innerException)
{
}

public class AriaTaskService : IAriaTaskService
{
    private sealed class TrackerEndpointState(string baselineValue)
    {
        public string BaselineValue { get; } = baselineValue;
        public bool OverrideApplied { get; set; }
    }

    private readonly IAriaProcessService _processService;
    private readonly IAriaRpcClient _rpcClient;
    private readonly ISettingsService _settingsService;
    private readonly ITrackerService _trackerService;
    private readonly IFileSystemService _fileSystemService;
    private readonly object _taskLock = new();
    private readonly object _backgroundTaskLock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _connectionManagementLock = new(1, 1);
    private readonly SemaphoreSlim _settingsOperationLock = new(1, 1);
    private readonly SemaphoreSlim _addOperationLock = new(1, 1);
    private readonly SemaphoreSlim _trackerOperationLock = new(1, 1);
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly List<Task> _eventRefreshFailures = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private Task? _eventRefreshTask;
    private int _isPolling;
    private int _eventRefreshRequested;
    private int _eventRefreshWorkerRunning;
    private int _isDisposed;
    private bool _shutdownCompleted;
    private Exception? _shutdownFailure;
    private DateTime _lastReconnectAttempt = DateTime.MinValue;
    private readonly object _reconnectLock = new();
    private Task? _reconnectTask;

    private List<AriaTaskInfo> _activeTasks = new();
    private List<AriaTaskInfo> _waitingTasks = new();
    private List<AriaTaskInfo> _stoppedTasks = new();
    private AppSettings? _appliedSettings;
    private readonly Dictionary<string, TrackerEndpointState> _trackerEndpointStates =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _connectedTrackerEndpoint;

    public bool IsConnected => _rpcClient.IsConnected;
    public AriaGlobalStat GlobalStat { get; private set; } = new();

    public event EventHandler? TasksUpdated;
    public event EventHandler? GlobalStatUpdated;
    public event EventHandler<string>? NotificationReceived;

    public List<AriaTaskInfo> ActiveTasks
    {
        get { lock (_taskLock) return _activeTasks.ToList(); }
        private set { lock (_taskLock) _activeTasks = value; }
    }

    public List<AriaTaskInfo> WaitingTasks
    {
        get { lock (_taskLock) return _waitingTasks.ToList(); }
        private set { lock (_taskLock) _waitingTasks = value; }
    }

    public List<AriaTaskInfo> StoppedTasks
    {
        get { lock (_taskLock) return _stoppedTasks.ToList(); }
        private set { lock (_taskLock) _stoppedTasks = value; }
    }

    public AriaTaskService(
        IAriaProcessService processService,
        IAriaRpcClient rpcClient,
        ISettingsService settingsService,
        ITrackerService trackerService,
        IFileSystemService fileSystemService)
    {
        _processService = processService;
        _rpcClient = rpcClient;
        _settingsService = settingsService;
        _trackerService = trackerService;
        _fileSystemService = fileSystemService;

        _rpcClient.ConnectionStateChanged += OnConnectionStateChanged;
        _rpcClient.DownloadStarted += OnDownloadStarted;
        _rpcClient.DownloadPaused += OnDownloadPaused;
        _rpcClient.DownloadStopped += OnDownloadStopped;
        _rpcClient.DownloadCompleted += OnDownloadCompleted;
        _rpcClient.DownloadError += OnDownloadError;
    }

    private void OnDownloadStarted(object? sender, string gid)
    {
        RequestEventRefresh();
    }

    private void OnDownloadPaused(object? sender, string gid)
    {
        RequestEventRefresh();
    }

    private void OnDownloadStopped(object? sender, string gid)
    {
        RequestEventRefresh();
    }

    private void OnDownloadCompleted(object? sender, string gid)
    {
        var msg = $"下载已完成 (GID: {gid})";
        NotificationReceived?.Invoke(this, msg);
        WeakReferenceMessenger.Default.Send(new NotificationMessage(msg));
        RequestEventRefresh();
    }

    private void OnDownloadError(object? sender, string gid)
    {
        var msg = $"下载出错 (GID: {gid})";
        NotificationReceived?.Invoke(this, msg);
        WeakReferenceMessenger.Default.Send(new NotificationMessage(msg, IsError: true));
        RequestEventRefresh();
    }

    private void RequestEventRefresh()
    {
        Task? worker = null;
        lock (_backgroundTaskLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _eventRefreshTask?.IsFaulted == true)
            {
                return;
            }

            Interlocked.Exchange(ref _eventRefreshRequested, 1);
            if (Interlocked.CompareExchange(ref _eventRefreshWorkerRunning, 1, 0) == 0)
            {
                worker = RunEventRefreshWorkerAsync(_lifetimeCts.Token);
                _eventRefreshTask = worker;
            }
        }

        if (worker != null)
        {
            _ = worker.ContinueWith(
                task =>
                {
                    lock (_backgroundTaskLock)
                    {
                        _eventRefreshFailures.Add(task);
                    }
                    Console.Error.WriteLine($"[AriaTaskService] Event refresh failed: {task.Exception}");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunEventRefreshWorkerAsync(CancellationToken cancellationToken)
    {
        var completedSuccessfully = false;
        try
        {
            do
            {
                Interlocked.Exchange(ref _eventRefreshRequested, 0);
                await RefreshTasksAsync(cancellationToken);
            }
            while (Volatile.Read(ref _eventRefreshRequested) != 0 &&
                   Volatile.Read(ref _isDisposed) == 0);

            completedSuccessfully = true;
        }
        finally
        {
            Interlocked.Exchange(ref _eventRefreshWorkerRunning, 0);
            if (completedSuccessfully &&
                Volatile.Read(ref _eventRefreshRequested) != 0 &&
                Volatile.Read(ref _isDisposed) == 0)
            {
                RequestEventRefresh();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;

        await _connectionManagementLock.WaitAsync(operationToken);
        try
        {
            ThrowIfDisposed();
            await StopPollingAsync();
            operationToken.ThrowIfCancellationRequested();

            var settings = _settingsService.Settings;
            try
            {
                if (settings.AutoStartDaemon)
                {
                    await EnsureManagedDaemonStartedAsync(settings, operationToken);
                }

                await ConnectAndReplayRuntimeSettingsAsync(settings, operationToken);
                operationToken.ThrowIfCancellationRequested();
                _appliedSettings = settings.Clone();

                try
                {
                    var stat = await _rpcClient.GetGlobalStatAsync(operationToken);
                    GlobalStat = stat;
                    GlobalStatUpdated?.Invoke(this, EventArgs.Empty);
                    WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(GlobalStat, true));
                    await RefreshTasksCoreAsync(operationToken);
                }
                catch (Exception ex) when (!operationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[AriaTaskService] Initial refresh failed: {ex}");
                }

                StartPolling(operationToken);
            }
            catch (Exception) when (!operationToken.IsCancellationRequested)
            {
                TriggerIndependentReconnect();
                throw;
            }
        }
        finally
        {
            _connectionManagementLock.Release();
        }
    }

    private void StartPolling(CancellationToken operationToken)
    {
        Task pollTask;
        lock (_backgroundTaskLock)
        {
            ThrowIfDisposed();
            operationToken.ThrowIfCancellationRequested();
            if (_pollTask != null)
            {
                throw new InvalidOperationException("轮询任务已启动。");
            }

            _pollCts = new CancellationTokenSource();
            pollTask = RunPeriodicPollingAsync(_pollCts.Token);
            _pollTask = pollTask;
        }

        _ = pollTask.ContinueWith(
            task =>
            {
                Console.Error.WriteLine($"[AriaTaskService] Polling failed: {task.Exception}");
                WeakReferenceMessenger.Default.Send(
                    new NotificationMessage("任务状态轮询已停止；请查看错误日志。", IsError: true));
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task StopPollingAsync()
    {
        CancellationTokenSource? pollCts;
        Task? pollTask;
        lock (_backgroundTaskLock)
        {
            pollCts = _pollCts;
            pollTask = _pollTask;
            _pollCts = null;
            _pollTask = null;
        }

        if (pollCts == null)
        {
            return;
        }

        pollCts.Cancel();
        try
        {
            if (pollTask != null)
            {
                await pollTask;
            }
        }
        catch (OperationCanceledException) when (pollCts.IsCancellationRequested)
        {
            // Cancellation is the requested polling shutdown path.
        }
        finally
        {
            pollCts.Dispose();
        }
    }

    private async Task RunPeriodicPollingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaTaskService] Polling tick failed: {ex}");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;

        var connectionLockAcquired = false;
        try
        {
            await _connectionManagementLock.WaitAsync(cancellationToken);
            connectionLockAcquired = true;

            if (_rpcClient.IsConnected)
            {
                try
                {
                    var stat = await _rpcClient.GetGlobalStatAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    GlobalStat = stat;
                    GlobalStatUpdated?.Invoke(this, EventArgs.Empty);
                    WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(GlobalStat, IsConnected));
                    await RefreshTasksCoreAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AriaTaskService] Polling refresh failed: {ex}");
                }
            }
            else
            {
                WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(GlobalStat, false));
            }
        }
        finally
        {
            if (connectionLockAcquired)
            {
                _connectionManagementLock.Release();
            }
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        if (!_rpcClient.IsConnected)
        {
            TriggerIndependentReconnect();
        }
    }

    private void TriggerIndependentReconnect()
    {
        lock (_reconnectLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _rpcClient.IsConnected) return;
            if (_reconnectTask != null && !_reconnectTask.IsCompleted) return;

            _reconnectTask = RunIndependentReconnectLoopAsync(_lifetimeCts.Token);
        }
    }

    private async Task RunIndependentReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_rpcClient.IsConnected)
        {
            var elapsed = DateTime.UtcNow - _lastReconnectAttempt;
            if (elapsed < TimeSpan.FromSeconds(3))
            {
                var delay = TimeSpan.FromSeconds(3) - elapsed;
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            if (_rpcClient.IsConnected || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var connectionLockAcquired = false;
            try
            {
                await _connectionManagementLock.WaitAsync(cancellationToken);
                connectionLockAcquired = true;

                if (!_rpcClient.IsConnected)
                {
                    await TryReconnectCoreAsync(cancellationToken);
                }

                if (_rpcClient.IsConnected)
                {
                    try
                    {
                        var stat = await _rpcClient.GetGlobalStatAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        GlobalStat = stat;
                        GlobalStatUpdated?.Invoke(this, EventArgs.Empty);
                        WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(GlobalStat, true));
                        await RefreshTasksCoreAsync(cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"[AriaTaskService] Post-reconnect refresh failed: {ex}");
                    }

                    EnsurePollingRunning();
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaTaskService] Independent reconnect cycle error: {ex}");
            }
            finally
            {
                if (connectionLockAcquired)
                {
                    _connectionManagementLock.Release();
                }
            }
        }
    }

    private async Task TryReconnectCoreAsync(CancellationToken cancellationToken)
    {
        _lastReconnectAttempt = DateTime.UtcNow;
        try
        {
            if (!_rpcClient.IsConnected)
            {
                var settings = _settingsService.Settings;
                if (settings.AutoStartDaemon && !_processService.IsRunning)
                {
                    await EnsureManagedDaemonStartedAsync(settings, cancellationToken);
                }

                await ConnectAndReplayRuntimeSettingsAsync(settings, cancellationToken);
                _appliedSettings = settings.Clone();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[AriaTaskService] Reconnect failed: {ex}");
            WeakReferenceMessenger.Default.Send(
                new NotificationMessage($"重新连接 Aria2 失败: {ex.Message}", IsError: true));
        }
    }

    private void EnsurePollingRunning()
    {
        lock (_backgroundTaskLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0) return;
            if (_pollTask == null || _pollTask.IsCompleted)
            {
                _pollCts?.Dispose();
                _pollCts = new CancellationTokenSource();
                var pollTask = RunPeriodicPollingAsync(_pollCts.Token);
                _pollTask = pollTask;
                _ = pollTask.ContinueWith(
                    task =>
                    {
                        Console.Error.WriteLine($"[AriaTaskService] Polling failed: {task.Exception}");
                        WeakReferenceMessenger.Default.Send(
                            new NotificationMessage("任务状态轮询已停止；请查看错误日志。", IsError: true));
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    public async Task RefreshTasksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;

        await _connectionManagementLock.WaitAsync(operationToken);
        try
        {
            ThrowIfDisposed();
            await RefreshTasksCoreAsync(operationToken);
        }
        finally
        {
            _connectionManagementLock.Release();
        }
    }

    private async Task RefreshTasksCoreAsync(CancellationToken cancellationToken)
    {
        if (!_rpcClient.IsConnected) throw new InvalidOperationException("Aria2 服务未连接。");

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_rpcClient.IsConnected) throw new InvalidOperationException("Aria2 服务未连接。");

            var active = await _rpcClient.TellActiveAsync(cancellationToken);
            var waiting = await _rpcClient.TellWaitingAsync(0, 100, cancellationToken);
            var stopped = await _rpcClient.TellStoppedAsync(0, 100, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            ActiveTasks = active;
            WaitingTasks = waiting;
            StoppedTasks = stopped;

            TasksUpdated?.Invoke(this, EventArgs.Empty);
            WeakReferenceMessenger.Default.Send(new TasksUpdatedMessage());
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<string> AddUriAsync(
        string url,
        string? saveDir = null,
        int? split = null,
        string? referer = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;

        await _addOperationLock.WaitAsync(operationToken);
        try
        {
            return await ExecuteConnectedOperationAsync(
                async token =>
                {
                    var options = new Dictionary<string, object>();
                    var settings = _settingsService.Settings;

                    var targetDir = !string.IsNullOrWhiteSpace(saveDir) ? saveDir : settings.DefaultDownloadDir;
                    if (!string.IsNullOrWhiteSpace(targetDir))
                    {
                        options["dir"] = targetDir;
                    }

                    if (split.HasValue && split.Value > 0)
                    {
                        options["split"] = split.Value.ToString();
                        options["max-connection-per-server"] = split.Value.ToString();
                    }

                    if (!string.IsNullOrWhiteSpace(referer))
                    {
                        options["referer"] = referer;
                    }

                    if (!string.IsNullOrWhiteSpace(userAgent))
                    {
                        options["user-agent"] = userAgent;
                    }

                    var urls = url.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(u => u.Trim())
                                  .Where(u => !string.IsNullOrEmpty(u))
                                  .ToList();

                    if (urls.Count == 0) throw new ArgumentException("下载链接不能为空。");

                    var gids = new List<string>();
                    foreach (var u in urls)
                    {
                        token.ThrowIfCancellationRequested();
                        var gid = await _rpcClient.AddUriAsync(new[] { u }, options, token);
                        gids.Add(gid);
                    }

                    return string.Join(",", gids);
                },
                refreshAfterOperation: true,
                operationToken);
        }
        finally
        {
            _addOperationLock.Release();
        }
    }

    public async Task<string> AddTorrentAsync(string filePath, string? saveDir = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;

        await _addOperationLock.WaitAsync(operationToken);
        try
        {
            return await ExecuteConnectedOperationAsync(
                async token =>
                {
                    if (!File.Exists(filePath)) throw new FileNotFoundException("种子文件不存在。", filePath);

                    var bytes = await File.ReadAllBytesAsync(filePath, token);
                    var options = new Dictionary<string, object>();
                    var settings = _settingsService.Settings;

                    var targetDir = !string.IsNullOrWhiteSpace(saveDir) ? saveDir : settings.DefaultDownloadDir;
                    if (!string.IsNullOrWhiteSpace(targetDir))
                    {
                        options["dir"] = targetDir;
                    }

                    return await _rpcClient.AddTorrentAsync(bytes, options, token);
                },
                refreshAfterOperation: true,
                operationToken);
        }
        finally
        {
            _addOperationLock.Release();
        }
    }

    public Task PauseTaskAsync(string gid, CancellationToken cancellationToken = default) =>
        ExecuteConnectedOperationAsync(
            async token => _ = await _rpcClient.PauseAsync(gid, token),
            refreshAfterOperation: true,
            cancellationToken);

    public Task ResumeTaskAsync(string gid, CancellationToken cancellationToken = default) =>
        ExecuteConnectedOperationAsync(
            async token => _ = await _rpcClient.UnpauseAsync(gid, token),
            refreshAfterOperation: true,
            cancellationToken);

    public Task RemoveTaskAsync(
        string gid,
        bool deleteFile = false,
        CancellationToken cancellationToken = default) =>
        ExecuteConnectedOperationAsync(
            async token =>
            {
                var taskInfo = await _rpcClient.TellStatusAsync(gid, token);
                var filesToDelete = deleteFile
                    ? PrepareFilesForDeletion(taskInfo.Files)
                    : Array.Empty<string>();

                switch (taskInfo.Status)
                {
                    case "active":
                    case "waiting":
                    case "paused":
                        await _rpcClient.RemoveAsync(gid, token);
                        break;
                    case "complete":
                    case "error":
                    case "removed":
                        await _rpcClient.RemoveDownloadResultAsync(gid, token);
                        break;
                    default:
                        throw new InvalidDataException(
                            $"任务 {gid} 返回未知状态，拒绝猜测删除方式: {taskInfo.Status}");
                }

                foreach (var fullPath in filesToDelete)
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }

                    var aria2File = fullPath + ".aria2";
                    if (File.Exists(aria2File))
                    {
                        File.Delete(aria2File);
                    }
                }
            },
            refreshAfterOperation: true,
            cancellationToken);

    private IReadOnlyList<string> PrepareFilesForDeletion(IReadOnlyList<AriaFile>? files)
    {
        var paths = files?
            .Select(static file => file.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray() ?? Array.Empty<string>();
        if (paths.Length == 0)
        {
            throw new InvalidDataException("任务未返回可验证的文件路径，拒绝先移除任务再猜测本地文件。");
        }

        var defaultDir = _settingsService.Settings.DefaultDownloadDir;
        if (string.IsNullOrWhiteSpace(defaultDir) || !Directory.Exists(defaultDir))
        {
            throw new DirectoryNotFoundException("下载目录不可用，拒绝在移除任务后删除本地文件。");
        }

        var safeRoot = Path.GetFullPath(defaultDir);
        var safePrefix = safeRoot.EndsWith(Path.DirectorySeparatorChar)
            ? safeRoot
            : safeRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var fullPaths = paths.Select(Path.GetFullPath).ToArray();
        foreach (var fullPath in fullPaths)
        {
            if (!fullPath.StartsWith(safePrefix, pathComparison))
            {
                throw new UnauthorizedAccessException($"拒绝删除下载目录之外的文件: {fullPath}");
            }

            EnsureDeletionPathHasNoLinkedDirectories(safeRoot, fullPath, pathComparison);
            if (Directory.Exists(fullPath))
            {
                throw new InvalidDataException($"任务文件路径实际指向目录，拒绝删除: {fullPath}");
            }
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return fullPaths.Distinct(pathComparer).ToArray();
    }

    private static void EnsureDeletionPathHasNoLinkedDirectories(
        string safeRoot,
        string fullPath,
        StringComparison pathComparison)
    {
        for (var directory = Directory.GetParent(fullPath);
             directory != null;
             directory = directory.Parent)
        {
            if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"删除路径包含符号链接或重解析目录，拒绝删除: {directory.FullName}");
            }

            if (string.Equals(directory.FullName, safeRoot, pathComparison))
            {
                return;
            }
        }

        throw new UnauthorizedAccessException($"无法证明文件位于下载目录内: {fullPath}");
    }

    public Task PauseAllTasksAsync(CancellationToken cancellationToken = default) =>
        ExecuteConnectedOperationAsync(
            async token => _ = await _rpcClient.PauseAllAsync(token),
            refreshAfterOperation: true,
            cancellationToken);

    public Task ResumeAllTasksAsync(CancellationToken cancellationToken = default) =>
        ExecuteConnectedOperationAsync(
            async token => _ = await _rpcClient.UnpauseAllAsync(token),
            refreshAfterOperation: true,
            cancellationToken);

    public Task PurgeCompletedTasksAsync(CancellationToken cancellationToken = default) =>
        ExecuteConnectedOperationAsync(
            async token => _ = await _rpcClient.PurgeDownloadResultAsync(token),
            refreshAfterOperation: true,
            cancellationToken);

    public Task ApplySpeedLimitAsync(
        long downloadLimitBytes,
        long uploadLimitBytes,
        CancellationToken cancellationToken = default) =>
        ExecuteConnectedOperationAsync(
            token => ApplySpeedLimitCoreAsync(downloadLimitBytes, uploadLimitBytes, token),
            refreshAfterOperation: false,
            cancellationToken);

    private async Task ExecuteConnectedOperationAsync(
        Func<CancellationToken, Task> operation,
        bool refreshAfterOperation,
        CancellationToken cancellationToken)
    {
        _ = await ExecuteConnectedOperationAsync(
            async token =>
            {
                await operation(token);
                return true;
            },
            refreshAfterOperation,
            cancellationToken);
    }

    private async Task<T> ExecuteConnectedOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        bool refreshAfterOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;

        await _connectionManagementLock.WaitAsync(operationToken);
        try
        {
            ThrowIfDisposed();
            if (!_rpcClient.IsConnected)
            {
                throw new InvalidOperationException("Aria2 服务未连接。");
            }

            var result = await operation(operationToken);
            operationToken.ThrowIfCancellationRequested();
            if (refreshAfterOperation)
            {
                await RefreshTasksCoreAsync(operationToken);
            }

            return result;
        }
        finally
        {
            _connectionManagementLock.Release();
        }
    }

    public async Task SaveAndApplySettingsAsync(
        AppSettings currentSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ThrowIfDisposed();

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;

        await _settingsOperationLock.WaitAsync(operationToken);
        try
        {
            ThrowIfDisposed();
            var previousSettings = _settingsService.Settings;
            await _settingsService.SaveAsync(currentSettings);
            var persistedSettings = _settingsService.Settings;
            try
            {
                await ApplySettingsCoreAsync(previousSettings, persistedSettings, operationToken);
            }
            catch (Exception ex)
            {
                throw new SettingsApplicationException(
                    "配置已保存，但应用到 aria2 运行态失败。",
                    ex);
            }
        }
        finally
        {
            _settingsOperationLock.Release();
        }
    }

    private async Task ApplySettingsCoreAsync(
        AppSettings previousSettings,
        AppSettings currentSettings,
        CancellationToken cancellationToken)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;

        await _connectionManagementLock.WaitAsync(operationToken);
        try
        {
            ThrowIfDisposed();
            var hadAppliedSettings = _appliedSettings != null;
            _appliedSettings ??= previousSettings.Clone();
            var appliedSettings = _appliedSettings;
            var endpointChanged = HasRpcEndpointChanged(appliedSettings, currentSettings);
            var daemonChanged = HasManagedDaemonConfigurationChanged(appliedSettings, currentSettings);
            var autoStartChanged = appliedSettings.AutoStartDaemon != currentSettings.AutoStartDaemon;
            var runtimeSettingsChanged = HasRuntimeSettingsChanged(appliedSettings, currentSettings);
            var trackerSettingsChanged = HasTrackerSettingsChanged(appliedSettings, currentSettings);
            var applyTrackerChange = trackerSettingsChanged &&
                (HasEnabledTrackerValue(appliedSettings) || HasEnabledTrackerValue(currentSettings));
            var managedDaemonWasRunning = _processService.IsRunning;

            if (!managedDaemonWasRunning &&
                !currentSettings.AutoStartDaemon &&
                HasManagedDaemonOnlyConfigurationChanged(appliedSettings, currentSettings))
            {
                throw new InvalidOperationException(
                    "当前使用外部 aria2 实例，无法应用仅属于本地 daemon 的配置。");
            }

            if (managedDaemonWasRunning && (daemonChanged || autoStartChanged))
            {
                operationToken.ThrowIfCancellationRequested();
                InvalidateTrackerBaseline();
                await _rpcClient.DisconnectAsync();
                operationToken.ThrowIfCancellationRequested();
                await _processService.StopDaemonAsync(operationToken);

                if (currentSettings.AutoStartDaemon)
                {
                    await EnsureManagedDaemonStartedAsync(currentSettings, operationToken);
                    await ConnectAndReplayRuntimeSettingsAsync(currentSettings, operationToken);
                }
                else if (endpointChanged)
                {
                    await ConnectAndReplayRuntimeSettingsAsync(currentSettings, operationToken);
                }
            }
            else if (!managedDaemonWasRunning && currentSettings.AutoStartDaemon &&
                     (autoStartChanged || daemonChanged))
            {
                operationToken.ThrowIfCancellationRequested();
                await RestoreCurrentTrackerOverrideAsync(operationToken);
                await _rpcClient.DisconnectAsync();
                await EnsureManagedDaemonStartedAsync(currentSettings, operationToken);
                await ConnectAndReplayRuntimeSettingsAsync(currentSettings, operationToken);

                if (daemonChanged && !_processService.IsRunning)
                {
                    throw new InvalidOperationException("当前连接由外部 aria2 实例提供，无法应用本地 daemon 配置。");
                }
            }
            else if (endpointChanged)
            {
                operationToken.ThrowIfCancellationRequested();
                await RestoreCurrentTrackerOverrideAsync(operationToken);
                await _rpcClient.DisconnectAsync();
                await ConnectAndReplayRuntimeSettingsAsync(currentSettings, operationToken);
            }
            else if (runtimeSettingsChanged)
            {
                if (_rpcClient.IsConnected)
                {
                    await ApplyRuntimeSettingsChangesCoreAsync(
                        currentSettings,
                        applyTrackerChange,
                        operationToken);
                }
                else
                {
                    await ConnectAndReplayRuntimeSettingsAsync(currentSettings, operationToken);
                }
            }
            else if (!hadAppliedSettings)
            {
                if (currentSettings.AutoStartDaemon && !_processService.IsRunning)
                {
                    await EnsureManagedDaemonStartedAsync(currentSettings, operationToken);
                }

                if (_rpcClient.IsConnected)
                {
                    await ReplayRuntimeSettingsCoreAsync(currentSettings, operationToken);
                }
                else
                {
                    await ConnectAndReplayRuntimeSettingsAsync(currentSettings, operationToken);
                }
            }

            operationToken.ThrowIfCancellationRequested();
            _appliedSettings = currentSettings.Clone();
        }
        finally
        {
            _connectionManagementLock.Release();
        }
    }

    private static bool HasRpcEndpointChanged(AppSettings previous, AppSettings current) =>
        !string.Equals(previous.RpcHost, current.RpcHost, StringComparison.OrdinalIgnoreCase) ||
        previous.RpcPort != current.RpcPort ||
        previous.RpcUseTls != current.RpcUseTls ||
        !string.Equals(previous.RpcSecret, current.RpcSecret, StringComparison.Ordinal);

    private static bool HasManagedDaemonConfigurationChanged(AppSettings previous, AppSettings current) =>
        !string.Equals(previous.Aria2ExecutablePath, current.Aria2ExecutablePath, StringComparison.Ordinal) ||
        previous.RpcPort != current.RpcPort ||
        !string.Equals(previous.RpcSecret, current.RpcSecret, StringComparison.Ordinal) ||
        !string.Equals(previous.DefaultDownloadDir, current.DefaultDownloadDir, StringComparison.Ordinal) ||
        previous.MaxConcurrentDownloads != current.MaxConcurrentDownloads ||
        previous.MaxConnectionPerServer != current.MaxConnectionPerServer ||
        previous.Split != current.Split ||
        previous.AllowInvalidCert != current.AllowInvalidCert;

    private static bool HasManagedDaemonOnlyConfigurationChanged(AppSettings previous, AppSettings current) =>
        !string.Equals(previous.Aria2ExecutablePath, current.Aria2ExecutablePath, StringComparison.Ordinal) ||
        previous.MaxConcurrentDownloads != current.MaxConcurrentDownloads ||
        previous.MaxConnectionPerServer != current.MaxConnectionPerServer ||
        previous.AllowInvalidCert != current.AllowInvalidCert;

    private static bool HasRuntimeSettingsChanged(AppSettings previous, AppSettings current) =>
        previous.MaxOverallDownloadLimit != current.MaxOverallDownloadLimit ||
        previous.MaxOverallUploadLimit != current.MaxOverallUploadLimit ||
        HasTrackerSettingsChanged(previous, current);

    private static bool HasTrackerSettingsChanged(AppSettings previous, AppSettings current) =>
        previous.EnableBtTrackers != current.EnableBtTrackers ||
        !string.Equals(previous.ExtraTrackers, current.ExtraTrackers, StringComparison.Ordinal);

    private async Task EnsureManagedDaemonStartedAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var wasManagedProcessRunning = _processService.IsRunning;
        if (!await _processService.StartDaemonAsync(settings, cancellationToken))
        {
            throw new InvalidOperationException("无法启动 aria2 daemon；请检查 aria2c 路径、端口和 RPC 配置。");
        }

        if (!_processService.IsRunning)
        {
            throw new InvalidOperationException("aria2 daemon 启动返回成功，但没有可管理的进程实例。");
        }

        if (!wasManagedProcessRunning && _processService.IsRunning)
        {
            InvalidateTrackerBaseline();
        }
    }

    private async Task ConnectAndReplayRuntimeSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await _rpcClient.ConnectAsync(
            settings.RpcHost,
            settings.RpcPort,
            settings.RpcSecret,
            settings.RpcUseTls,
            cancellationToken);
        _lastReconnectAttempt = DateTime.UtcNow;
        _connectedTrackerEndpoint = GetRpcEndpointKey(settings);

        try
        {
            await ReplayRuntimeSettingsCoreAsync(settings, cancellationToken);
        }
        catch (Exception operationException)
        {
            _connectedTrackerEndpoint = null;
            try
            {
                await _rpcClient.DisconnectAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "RPC 连接后应用运行时设置失败，且连接清理也失败。",
                    operationException,
                    cleanupException);
            }

            throw;
        }
    }

    private async Task ApplySpeedLimitCoreAsync(
        long downloadLimitBytes,
        long uploadLimitBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new Dictionary<string, object>
        {
            { "max-overall-download-limit", downloadLimitBytes.ToString() },
            { "max-overall-upload-limit", uploadLimitBytes.ToString() }
        };
        await _rpcClient.ChangeGlobalOptionAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task ReplayRuntimeSettingsCoreAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, object>
        {
            { "max-overall-download-limit", settings.MaxOverallDownloadLimit.ToString() },
            { "max-overall-upload-limit", settings.MaxOverallUploadLimit.ToString() }
        };

        TrackerEndpointState? trackerState = null;
        bool? overrideApplied = null;
        if (TryGetEnabledTrackerValue(settings, out var trackerValue))
        {
            trackerState = await EnsureTrackerEndpointStateAsync(settings, cancellationToken);
            options["bt-tracker"] = trackerValue;
            overrideApplied = true;
        }
        else if (TryGetTrackerEndpointState(settings, out trackerState) &&
                 trackerState.OverrideApplied)
        {
            options["bt-tracker"] = trackerState.BaselineValue;
            overrideApplied = false;
        }

        await _rpcClient.ChangeGlobalOptionAsync(options, cancellationToken);
        if (overrideApplied.HasValue)
        {
            trackerState!.OverrideApplied = overrideApplied.Value;
        }
    }

    private async Task ApplyRuntimeSettingsChangesCoreAsync(
        AppSettings settings,
        bool applyTrackerChange,
        CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, object>
        {
            { "max-overall-download-limit", settings.MaxOverallDownloadLimit.ToString() },
            { "max-overall-upload-limit", settings.MaxOverallUploadLimit.ToString() }
        };

        TrackerEndpointState? trackerState = null;
        bool? overrideApplied = null;
        if (applyTrackerChange)
        {
            trackerState = await EnsureTrackerEndpointStateAsync(settings, cancellationToken);
            options["bt-tracker"] = TryGetEnabledTrackerValue(settings, out var trackerValue)
                ? trackerValue
                : trackerState.BaselineValue;
            overrideApplied = TryGetEnabledTrackerValue(settings, out _);
        }

        await _rpcClient.ChangeGlobalOptionAsync(options, cancellationToken);
        if (overrideApplied.HasValue)
        {
            trackerState!.OverrideApplied = overrideApplied.Value;
        }
    }

    private async Task<TrackerEndpointState> EnsureTrackerEndpointStateAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var endpoint = GetRpcEndpointKey(settings);
        EnsureConnectedTrackerEndpoint(endpoint);
        if (_trackerEndpointStates.TryGetValue(endpoint, out var existingState))
        {
            return existingState;
        }

        var options = await _rpcClient.GetGlobalOptionAsync(cancellationToken);
        var baselineValue = options.TryGetValue("bt-tracker", out var trackerValue)
            ? trackerValue
            : string.Empty;
        var state = new TrackerEndpointState(baselineValue);
        _trackerEndpointStates.Add(endpoint, state);
        return state;
    }

    private bool TryGetTrackerEndpointState(
        AppSettings settings,
        out TrackerEndpointState state)
    {
        var endpoint = GetRpcEndpointKey(settings);
        EnsureConnectedTrackerEndpoint(endpoint);
        return _trackerEndpointStates.TryGetValue(endpoint, out state!);
    }

    private void EnsureConnectedTrackerEndpoint(string expectedEndpoint)
    {
        if (!string.Equals(
                _connectedTrackerEndpoint,
                expectedEndpoint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "当前 RPC 连接与待应用设置的 endpoint 不一致。");
        }
    }

    private void InvalidateTrackerBaseline()
    {
        if (_connectedTrackerEndpoint != null)
        {
            _trackerEndpointStates.Remove(_connectedTrackerEndpoint);
            _connectedTrackerEndpoint = null;
        }
    }

    private async Task RestoreCurrentTrackerOverrideAsync(CancellationToken cancellationToken)
    {
        if (!_rpcClient.IsConnected ||
            _connectedTrackerEndpoint == null ||
            !_trackerEndpointStates.TryGetValue(_connectedTrackerEndpoint, out var state) ||
            !state.OverrideApplied)
        {
            return;
        }

        await _rpcClient.ChangeGlobalOptionAsync(
            new Dictionary<string, object> { { "bt-tracker", state.BaselineValue } },
            cancellationToken);
        state.OverrideApplied = false;
    }

    private static string GetRpcEndpointKey(AppSettings settings) =>
        $"{(settings.RpcUseTls ? "wss" : "ws")}://{NormalizeEndpointHost(settings)}:{settings.RpcPort}";

    private static string NormalizeEndpointHost(AppSettings settings)
    {
        var host = settings.RpcHost.Trim().Trim('[', ']');
        return IPAddress.TryParse(host, out var address)
            ? address.ToString()
            : host.ToLowerInvariant();
    }

    private static string NormalizeTrackers(string trackers) =>
        string.Join(",", trackers
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static tracker => tracker.Trim())
            .Where(static tracker => tracker.Length > 0));

    private static bool TryGetEnabledTrackerValue(AppSettings settings, out string trackerValue)
    {
        trackerValue = NormalizeTrackers(settings.ExtraTrackers);
        return settings.EnableBtTrackers && trackerValue.Length > 0;
    }

    private static bool HasEnabledTrackerValue(AppSettings settings) =>
        settings.EnableBtTrackers && NormalizeTrackers(settings.ExtraTrackers).Length > 0;

    public async Task UpdateTrackersAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCts.Token;
        await _trackerOperationLock.WaitAsync(operationToken);
        try
        {
            ThrowIfDisposed();
            var customTrackersUrl = _settingsService.Settings.CustomTrackersUrl;
            if (string.IsNullOrWhiteSpace(customTrackersUrl))
            {
                throw new InvalidOperationException("未配置 Tracker 订阅地址。");
            }

            var trackers = await _trackerService.FetchTrackersAsync(
                customTrackersUrl,
                operationToken);
            if (trackers.Count == 0)
            {
                throw new InvalidDataException("Tracker 订阅未返回任何合法地址。");
            }

            await _settingsOperationLock.WaitAsync(operationToken);
            try
            {
                ThrowIfDisposed();
                if (!string.Equals(
                        _settingsService.Settings.CustomTrackersUrl,
                        customTrackersUrl,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Tracker 订阅地址在下载期间已改变，请重新执行更新。");
                }

                var trackerText = string.Join("\n", trackers);
                _ = await _settingsService.UpdateAsync(
                    settings => settings.ExtraTrackers = trackerText);

                try
                {
                    await _connectionManagementLock.WaitAsync(operationToken);
                    try
                    {
                        var currentSettings = _settingsService.Settings;
                        if (_rpcClient.IsConnected &&
                            TryGetEnabledTrackerValue(currentSettings, out var trackerValue))
                        {
                            var trackerState = await EnsureTrackerEndpointStateAsync(
                                currentSettings,
                                operationToken);
                            await _rpcClient.ChangeGlobalOptionAsync(
                                new Dictionary<string, object> { { "bt-tracker", trackerValue } },
                                operationToken);
                            trackerState.OverrideApplied = true;
                        }
                        CopyTrackerSettings(currentSettings, _appliedSettings);
                    }
                    finally
                    {
                        _connectionManagementLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    throw new SettingsApplicationException(
                        "Tracker 列表已保存，但应用到 aria2 运行态失败。",
                        ex);
                }
            }
            finally
            {
                _settingsOperationLock.Release();
            }
        }
        finally
        {
            _trackerOperationLock.Release();
        }
    }

    private static void CopyTrackerSettings(AppSettings source, AppSettings? target)
    {
        if (target == null)
        {
            return;
        }

        target.EnableBtTrackers = source.EnableBtTrackers;
        target.ExtraTrackers = source.ExtraTrackers;
    }

    public void OpenFile(string filePath) => _fileSystemService.OpenFile(filePath);

    public void OpenDirectory(string directoryPath) => _fileSystemService.OpenDirectory(directoryPath);

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _shutdownLock.WaitAsync(cancellationToken);
        Exception? shutdownFailure;
        try
        {
            if (_shutdownCompleted)
            {
                shutdownFailure = _shutdownFailure;
            }
            else
            {
                shutdownFailure = await ShutdownCoreAsync(CancellationToken.None);
                _shutdownFailure = shutdownFailure;
                _shutdownCompleted = true;
            }
        }
        finally
        {
            _shutdownLock.Release();
        }

        if (shutdownFailure != null)
        {
            ExceptionDispatchInfo.Capture(shutdownFailure).Throw();
        }
    }

    private async Task<Exception?> ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        try
        {
            BeginShutdown();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        var connectionLockAcquired = false;
        try
        {
            await _connectionManagementLock.WaitAsync(cancellationToken);
            connectionLockAcquired = true;

            Task<string>? managedDaemonShutdownTask = null;
            var managedDaemonShutdownObserved = false;
            if (_rpcClient.IsConnected && !_processService.IsRunning)
            {
                try
                {
                    await RestoreCurrentTrackerOverrideAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (_rpcClient.IsConnected && _processService.IsRunning)
            {
                managedDaemonShutdownTask = _rpcClient.ShutdownAsync(cancellationToken);
                var completedTask = await Task.WhenAny(
                    managedDaemonShutdownTask,
                    Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken));
                if (ReferenceEquals(completedTask, managedDaemonShutdownTask))
                {
                    managedDaemonShutdownObserved = true;
                    try
                    {
                        await managedDaemonShutdownTask;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }
                else
                {
                    failures.Add(new TimeoutException(
                        "Timed out while requesting aria2 RPC shutdown."));
                }
            }

            try
            {
                await _rpcClient.DisconnectAsync();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }

            if (managedDaemonShutdownTask != null && !managedDaemonShutdownObserved)
            {
                await CaptureBackgroundFailureAsync(managedDaemonShutdownTask, failures);
            }

            if (_rpcClient.IsConnected)
            {
                failures.Add(new InvalidOperationException(
                    "RPC 连接在任务服务关闭后仍处于连接状态。"));
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        finally
        {
            if (connectionLockAcquired)
            {
                _connectionManagementLock.Release();
            }
        }

        CancellationTokenSource? pollCts;
        Task? pollTask;
        Task[] eventRefreshTasks;
        lock (_backgroundTaskLock)
        {
            pollCts = _pollCts;
            pollTask = _pollTask;
            eventRefreshTasks = _eventRefreshFailures
                .Append(_eventRefreshTask)
                .Where(task => task != null)
                .Cast<Task>()
                .Distinct()
                .ToArray();
            _pollCts = null;
            _pollTask = null;
            _eventRefreshTask = null;
            _eventRefreshFailures.Clear();
        }

        if (pollCts != null)
        {
            try
            {
                pollCts.Cancel();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        Task? reconnectTask;
        lock (_reconnectLock)
        {
            reconnectTask = _reconnectTask;
            _reconnectTask = null;
        }

        await CaptureBackgroundFailureAsync(reconnectTask, failures);
        await CaptureBackgroundFailureAsync(pollTask, failures);
        foreach (var eventRefreshTask in eventRefreshTasks)
        {
            await CaptureBackgroundFailureAsync(eventRefreshTask, failures);
        }
        pollCts?.Dispose();

        await DrainOperationAsync(_addOperationLock, cancellationToken, failures);
        await DrainOperationAsync(_settingsOperationLock, cancellationToken, failures);
        await DrainOperationAsync(_trackerOperationLock, cancellationToken, failures);

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("任务服务关闭时发生多个错误。", failures)
        };
    }

    private static async Task DrainOperationAsync(
        SemaphoreSlim operationLock,
        CancellationToken cancellationToken,
        List<Exception> failures)
    {
        var acquired = false;
        try
        {
            await operationLock.WaitAsync(cancellationToken);
            acquired = true;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        finally
        {
            if (acquired)
            {
                operationLock.Release();
            }
        }
    }

    private async Task CaptureBackgroundFailureAsync(Task? task, List<Exception> failures)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Cancellation is the requested service shutdown path.
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _eventRefreshRequested, 0);
        _rpcClient.ConnectionStateChanged -= OnConnectionStateChanged;
        _rpcClient.DownloadStarted -= OnDownloadStarted;
        _rpcClient.DownloadPaused -= OnDownloadPaused;
        _rpcClient.DownloadStopped -= OnDownloadStopped;
        _rpcClient.DownloadCompleted -= OnDownloadCompleted;
        _rpcClient.DownloadError -= OnDownloadError;

        CancellationTokenSource? pollCts;
        lock (_backgroundTaskLock)
        {
            pollCts = _pollCts;
        }

        _lifetimeCts.Cancel();
        pollCts?.Cancel();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AriaTaskService));
        }
    }

    public void Dispose()
    {
        BeginShutdown();
        GC.SuppressFinalize(this);
    }
}
