using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using AriaUI.Helpers;
using AriaUI.Models;

namespace AriaUI.Services;

public interface IAriaTaskService
{
    bool IsConnected { get; }
    AriaGlobalStat GlobalStat { get; }
    List<AriaTaskInfo> ActiveTasks { get; }
    List<AriaTaskInfo> WaitingTasks { get; }
    List<AriaTaskInfo> StoppedTasks { get; }
    event EventHandler? TasksUpdated;
    event EventHandler? GlobalStatUpdated;
    event EventHandler<string>? NotificationReceived;

    Task InitializeAsync();
    Task RefreshTasksAsync();
    Task<string> AddUriAsync(string url, string? saveDir = null, int? split = null, string? referer = null, string? userAgent = null);
    Task<string> AddTorrentAsync(string filePath, string? saveDir = null);
    Task PauseTaskAsync(string gid);
    Task ResumeTaskAsync(string gid);
    Task RemoveTaskAsync(string gid, bool deleteFile = false);
    Task PauseAllTasksAsync();
    Task ResumeAllTasksAsync();
    Task PurgeCompletedTasksAsync();
    Task ApplySpeedLimitAsync(long downloadLimitBytes, long uploadLimitBytes);
    Task UpdateTrackersAsync();
    void OpenFile(string filePath);
    void OpenDirectory(string directoryPath);
}

public class AriaTaskService : IAriaTaskService
{
    private readonly IAriaProcessService _processService;
    private readonly IAriaRpcClient _rpcClient;
    private readonly ISettingsService _settingsService;
    private readonly ITrackerService _trackerService;
    private readonly IFileSystemService _fileSystemService;
    private readonly Timer _pollTimer;
    private int _isPolling;

    public bool IsConnected => _rpcClient.IsConnected;
    public AriaGlobalStat GlobalStat { get; private set; } = new();

    public event EventHandler? TasksUpdated;
    public event EventHandler? GlobalStatUpdated;
    public event EventHandler<string>? NotificationReceived;

    public List<AriaTaskInfo> ActiveTasks { get; private set; } = new();
    public List<AriaTaskInfo> WaitingTasks { get; private set; } = new();
    public List<AriaTaskInfo> StoppedTasks { get; private set; } = new();

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

        _rpcClient.DownloadCompleted += (s, gid) =>
        {
            var msg = $"下载已完成 (GID: {gid})";
            NotificationReceived?.Invoke(this, msg);
            WeakReferenceMessenger.Default.Send(new NotificationMessage(msg));
            RefreshTasksAsync().SafeFireAndForget();
        };

        _rpcClient.DownloadError += (s, gid) =>
        {
            var msg = $"下载出错 (GID: {gid})";
            NotificationReceived?.Invoke(this, msg);
            WeakReferenceMessenger.Default.Send(new NotificationMessage(msg, IsError: true));
            RefreshTasksAsync().SafeFireAndForget();
        };

        _pollTimer = new Timer(async _ => await PollLoopAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync()
    {
        await _settingsService.LoadAsync();
        var settings = _settingsService.Settings;

        if (settings.AutoStartDaemon)
        {
            var started = await _processService.StartDaemonAsync(settings);
            if (!started)
            {
                Console.Error.WriteLine("[AriaTaskService] Failed to auto start aria2 daemon.");
            }
        }

        try
        {
            await _rpcClient.ConnectAsync(settings.RpcHost, settings.RpcPort, settings.RpcSecret);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaTaskService] Initial RPC connection error: {ex.Message}");
        }

        _pollTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private async Task PollLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;

        try
        {
            if (!_rpcClient.IsConnected)
            {
                var s = _settingsService.Settings;
                try
                {
                    await _rpcClient.ConnectAsync(s.RpcHost, s.RpcPort, s.RpcSecret);
                }
                catch { }
            }

            if (_rpcClient.IsConnected)
            {
                var stat = await _rpcClient.GetGlobalStatAsync();
                if (stat != null)
                {
                    GlobalStat = stat;
                    GlobalStatUpdated?.Invoke(this, EventArgs.Empty);
                    WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(GlobalStat, IsConnected));
                }

                await RefreshTasksAsync();
            }
            else
            {
                WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(GlobalStat, false));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaTaskService] Poll error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    public async Task RefreshTasksAsync()
    {
        if (!_rpcClient.IsConnected) return;

        try
        {
            var active = await _rpcClient.TellActiveAsync();
            var waiting = await _rpcClient.TellWaitingAsync(0, 100);
            var stopped = await _rpcClient.TellStoppedAsync(0, 100);

            ActiveTasks = active;
            WaitingTasks = waiting;
            StoppedTasks = stopped;

            TasksUpdated?.Invoke(this, EventArgs.Empty);
            WeakReferenceMessenger.Default.Send(new TasksUpdatedMessage());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaTaskService] Refresh tasks error: {ex.Message}");
        }
    }

    public async Task<string> AddUriAsync(string url, string? saveDir = null, int? split = null, string? referer = null, string? userAgent = null)
    {
        if (!_rpcClient.IsConnected) throw new InvalidOperationException("未连接到 Aria2 服务。");

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

        // Split multiple URLs if user entered multiple lines
        var urls = url.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(u => u.Trim())
                      .Where(u => !string.IsNullOrEmpty(u))
                      .ToList();

        if (urls.Count == 0) throw new ArgumentException("下载链接不能为空。");

        string lastGid = string.Empty;
        foreach (var u in urls)
        {
            lastGid = await _rpcClient.AddUriAsync(new[] { u }, options);
        }

        await RefreshTasksAsync();
        return lastGid;
    }

    public async Task<string> AddTorrentAsync(string filePath, string? saveDir = null)
    {
        if (!_rpcClient.IsConnected) throw new InvalidOperationException("未连接到 Aria2 服务。");
        if (!File.Exists(filePath)) throw new FileNotFoundException("种子文件不存在。", filePath);

        var bytes = await File.ReadAllBytesAsync(filePath);
        var options = new Dictionary<string, object>();
        var settings = _settingsService.Settings;

        var targetDir = !string.IsNullOrWhiteSpace(saveDir) ? saveDir : settings.DefaultDownloadDir;
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            options["dir"] = targetDir;
        }

        var gid = await _rpcClient.AddTorrentAsync(bytes, options);
        await RefreshTasksAsync();
        return gid;
    }

    public async Task PauseTaskAsync(string gid)
    {
        if (_rpcClient.IsConnected)
        {
            await _rpcClient.PauseAsync(gid);
            await RefreshTasksAsync();
        }
    }

    public async Task ResumeTaskAsync(string gid)
    {
        if (_rpcClient.IsConnected)
        {
            await _rpcClient.UnpauseAsync(gid);
            await RefreshTasksAsync();
        }
    }

    public async Task RemoveTaskAsync(string gid, bool deleteFile = false)
    {
        if (!_rpcClient.IsConnected) return;

        AriaTaskInfo? taskInfo = null;
        try
        {
            taskInfo = await _rpcClient.TellStatusAsync(gid);
        }
        catch { }

        try
        {
            await _rpcClient.RemoveAsync(gid);
        }
        catch
        {
            try
            {
                await _rpcClient.RemoveDownloadResultAsync(gid);
            }
            catch { }
        }

        if (deleteFile && taskInfo?.Files != null)
        {
            foreach (var file in taskInfo.Files)
            {
                if (!string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
                {
                    try { File.Delete(file.Path); } catch { }
                }
                var aria2File = file.Path + ".aria2";
                if (File.Exists(aria2File))
                {
                    try { File.Delete(aria2File); } catch { }
                }
            }
        }

        await RefreshTasksAsync();
    }

    public async Task PauseAllTasksAsync()
    {
        if (_rpcClient.IsConnected)
        {
            await _rpcClient.PauseAllAsync();
            await RefreshTasksAsync();
        }
    }

    public async Task ResumeAllTasksAsync()
    {
        if (_rpcClient.IsConnected)
        {
            await _rpcClient.UnpauseAllAsync();
            await RefreshTasksAsync();
        }
    }

    public async Task PurgeCompletedTasksAsync()
    {
        if (_rpcClient.IsConnected)
        {
            await _rpcClient.PurgeDownloadResultAsync();
            await RefreshTasksAsync();
        }
    }

    public async Task ApplySpeedLimitAsync(long downloadLimitBytes, long uploadLimitBytes)
    {
        if (_rpcClient.IsConnected)
        {
            var options = new Dictionary<string, object>
            {
                { "max-overall-download-limit", downloadLimitBytes.ToString() },
                { "max-overall-upload-limit", uploadLimitBytes.ToString() }
            };
            await _rpcClient.ChangeGlobalOptionAsync(options);
        }
    }

    public async Task UpdateTrackersAsync()
    {
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.CustomTrackersUrl)) return;

        var trackers = await _trackerService.FetchTrackersAsync(settings.CustomTrackersUrl);
        if (trackers.Count > 0)
        {
            var trackerStr = string.Join(",", trackers);
            settings.ExtraTrackers = string.Join("\n", trackers);
            await _settingsService.SaveAsync();

            if (_rpcClient.IsConnected)
            {
                var options = new Dictionary<string, object>
                {
                    { "bt-tracker", trackerStr }
                };
                await _rpcClient.ChangeGlobalOptionAsync(options);
            }
        }
    }

    public void OpenFile(string filePath) => _fileSystemService.OpenFile(filePath);

    public void OpenDirectory(string directoryPath) => _fileSystemService.OpenDirectory(directoryPath);
}
