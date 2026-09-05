using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using AriaUI.Models;
using AriaUI.Services.Engine;

namespace AriaUI.Services;

/// <summary>
/// Native IAriaEngine-backed implementation of IAriaTaskService (Phase 3 Step 1).
/// Directly drives the in-process IAriaEngine without running aria2c or opening control ports.
/// </summary>
public class AriaEngineTaskService : IAriaTaskService
{
    private readonly IAriaEngine _engine;
    private readonly ISettingsService _settingsService;
    private readonly ITrackerService _trackerService;
    private readonly IFileSystemService _fileSystemService;

    private readonly object _taskLock = new();
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _eventsWatchTask;
    private int _isDisposed;

    private List<AriaTaskInfo> _activeTasks = new();
    private List<AriaTaskInfo> _waitingTasks = new();
    private List<AriaTaskInfo> _stoppedTasks = new();
    private AriaGlobalStat _globalStat = new();

    public bool IsConnected => _engine.State == EngineState.Ready;

    public AriaGlobalStat GlobalStat
    {
        get { lock (_taskLock) return _globalStat; }
        private set { lock (_taskLock) _globalStat = value; }
    }

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

    public event EventHandler? TasksUpdated;
    public event EventHandler? GlobalStatUpdated;
    public event EventHandler<string>? NotificationReceived;

    public AriaEngineTaskService(
        IAriaEngine engine,
        ISettingsService settingsService,
        ITrackerService trackerService,
        IFileSystemService fileSystemService)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _trackerService = trackerService ?? throw new ArgumentNullException(nameof(trackerService));
        _fileSystemService = fileSystemService ?? throw new ArgumentNullException(nameof(fileSystemService));

        _engine.SnapshotUpdated += OnSnapshotUpdated;
        _engine.StateChanged += OnStateChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _opLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_engine.State == EngineState.Ready)
            {
                return;
            }

            var settings = _settingsService.Settings;

            // Resolve session file path under safe user config directory
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "ariaui");
            Directory.CreateDirectory(configDir);
            var sessionFilePath = Path.Combine(configDir, "aria2.session");

            var downloadDir = !string.IsNullOrWhiteSpace(settings.DefaultDownloadDir)
                ? settings.DefaultDownloadDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloadDir);

            var initialOptions = new Dictionary<string, string>
            {
                ["dir"] = downloadDir,
                ["max-concurrent-downloads"] = Math.Max(1, settings.MaxConcurrentDownloads).ToString(),
                ["max-overall-download-limit"] = Math.Max(0, settings.MaxOverallDownloadLimit).ToString(),
                ["max-overall-upload-limit"] = Math.Max(0, settings.MaxOverallUploadLimit).ToString(),
                ["split"] = Math.Max(1, settings.Split).ToString(),
                ["max-connection-per-server"] = Math.Max(1, settings.Split).ToString(),
                ["continue"] = "true",
                ["enable-rpc"] = "false"
            };

            var startOptions = new EngineStartOptions
            {
                SessionFilePath = sessionFilePath,
                SaveSessionIntervalSeconds = 30,
                DownloadDir = downloadDir,
                InitialOptions = initialOptions
            };

            await _engine.StartAsync(startOptions, cancellationToken);

            // Start event listening loop
            _eventsWatchTask = Task.Run(() => WatchEventsLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);

            // Initial snapshot load
            ApplySnapshot(_engine.CurrentSnapshot);

            if (settings.EnableBtTrackers && !string.IsNullOrWhiteSpace(settings.CustomTrackersUrl))
            {
                _ = UpdateTrackersAsync(_lifetimeCts.Token).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Console.Error.WriteLine($"[AriaEngineTaskService] Auto-update trackers failed: {t.Exception}");
                    }
                }, TaskScheduler.Default);
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    private void OnSnapshotUpdated(object? sender, EngineSnapshot snapshot)
    {
        ApplySnapshot(snapshot);
    }

    private void OnStateChanged(object? sender, EngineState state)
    {
        TasksUpdated?.Invoke(this, EventArgs.Empty);
        GlobalStatUpdated?.Invoke(this, EventArgs.Empty);
        WeakReferenceMessenger.Default.Send(new TasksUpdatedMessage());
        WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(GlobalStat, IsConnected));
    }

    private void ApplySnapshot(EngineSnapshot snapshot)
    {
        if (snapshot == null) return;

        lock (_taskLock)
        {
            _activeTasks = snapshot.ActiveTasks.ToList();
            _waitingTasks = snapshot.WaitingTasks.ToList();
            _stoppedTasks = snapshot.StoppedTasks.ToList();
            _globalStat = snapshot.GlobalStat;
        }

        TasksUpdated?.Invoke(this, EventArgs.Empty);
        GlobalStatUpdated?.Invoke(this, EventArgs.Empty);
        WeakReferenceMessenger.Default.Send(new TasksUpdatedMessage());
        WeakReferenceMessenger.Default.Send(new GlobalStatUpdatedMessage(_globalStat, IsConnected));
    }

    private async Task WatchEventsLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var ev in _engine.WatchEventsAsync(cancellationToken))
            {
                switch (ev.Type)
                {
                    case EngineEventType.DownloadComplete:
                    {
                        var msg = $"下载已完成 (GID: {ev.Gid})";
                        NotificationReceived?.Invoke(this, msg);
                        WeakReferenceMessenger.Default.Send(new NotificationMessage(msg));
                        break;
                    }
                    case EngineEventType.DownloadError:
                    {
                        var msg = $"下载出错 (GID: {ev.Gid})";
                        NotificationReceived?.Invoke(this, msg);
                        WeakReferenceMessenger.Default.Send(new NotificationMessage(msg, IsError: true));
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaEngineTaskService] Event watch error: {ex}");
        }
    }

    public async Task RefreshTasksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ApplySnapshot(_engine.CurrentSnapshot);
        await Task.CompletedTask;
    }

    public Task<string> AddUriAsync(
        string url,
        string? saveDir,
        int? split,
        string? referer,
        string? userAgent,
        CancellationToken cancellationToken) =>
        AddUriAsync(url, saveDir, split, referer, userAgent, headers: null, outFilename: null, cancellationToken);

    public async Task<string> AddUriAsync(
        string url,
        string? saveDir = null,
        int? split = null,
        string? referer = null,
        string? userAgent = null,
        IReadOnlyList<string>? headers = null,
        string? outFilename = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("下载链接不能为空。", nameof(url));

        var urls = url.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(u => u.Trim())
                      .Where(u => !string.IsNullOrEmpty(u))
                      .ToList();

        if (urls.Count == 0)
            throw new ArgumentException("下载链接不能为空。", nameof(url));

        var settings = _settingsService.Settings;
        var targetDir = !string.IsNullOrWhiteSpace(saveDir) ? saveDir : settings.DefaultDownloadDir;

        var options = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            options.Add(new KeyValuePair<string, string>("dir", targetDir));
        }

        if (split.HasValue && split.Value > 0)
        {
            options.Add(new KeyValuePair<string, string>("split", split.Value.ToString()));
            options.Add(new KeyValuePair<string, string>("max-connection-per-server", split.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(referer))
        {
            options.Add(new KeyValuePair<string, string>("referer", referer));
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            options.Add(new KeyValuePair<string, string>("user-agent", userAgent));
        }

        if (!string.IsNullOrWhiteSpace(outFilename))
        {
            options.Add(new KeyValuePair<string, string>("out", outFilename));
        }

        if (headers != null)
        {
            foreach (var h in headers)
            {
                if (!string.IsNullOrWhiteSpace(h))
                {
                    options.Add(new KeyValuePair<string, string>("header", h));
                }
            }
        }

        var gids = new List<string>();
        foreach (var u in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gid = await _engine.AddUriAsync(new[] { u }, options, cancellationToken);
            gids.Add(gid);
        }

        return string.Join(",", gids);
    }

    public async Task<string> AddTorrentAsync(
        string filePath,
        string? saveDir = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!File.Exists(filePath))
            throw new FileNotFoundException("种子文件不存在。", filePath);

        var settings = _settingsService.Settings;
        var targetDir = !string.IsNullOrWhiteSpace(saveDir) ? saveDir : settings.DefaultDownloadDir;

        var options = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            options.Add(new KeyValuePair<string, string>("dir", targetDir));
        }

        return await _engine.AddTorrentAsync(filePath, options, cancellationToken);
    }

    public async Task PauseTaskAsync(string gid, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engine.PauseAsync(gid, force: false, cancellationToken);
    }

    public async Task ResumeTaskAsync(string gid, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engine.ResumeAsync(gid, cancellationToken);
    }

    public async Task RemoveTaskAsync(
        string gid,
        bool deleteFile = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // 1. If files need to be deleted, resolve file paths and perform F03 boundary validation BEFORE stopping
        IReadOnlyList<string> filesToDelete = Array.Empty<string>();
        if (deleteFile)
        {
            var task = FindTask(gid);
            if (task?.Files != null && task.Files.Count > 0)
            {
                filesToDelete = PrepareFilesForDeletion(task.Files);
            }
        }

        // 2. Remove task from engine
        try
        {
            await _engine.RemoveAsync(gid, force: false, cancellationToken);
        }
        catch (EngineCommandException ex) when (ex.ErrorCode == 1) // already stopped or purged
        {
            // Ignore if task has already stopped
        }

        // 3. Perform file deletion
        foreach (var fullPath in filesToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    }

    private AriaTaskInfo? FindTask(string gid)
    {
        lock (_taskLock)
        {
            return _activeTasks.FirstOrDefault(t => t.Gid == gid)
                ?? _waitingTasks.FirstOrDefault(t => t.Gid == gid)
                ?? _stoppedTasks.FirstOrDefault(t => t.Gid == gid);
        }
    }

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

    public async Task PauseAllTasksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        List<AriaTaskInfo> active;
        lock (_taskLock) active = _activeTasks.ToList();

        foreach (var t in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _engine.PauseAsync(t.Gid, force: false, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaEngineTaskService] PauseAll failed for {t.Gid}: {ex.Message}");
            }
        }
    }

    public async Task ResumeAllTasksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        List<AriaTaskInfo> waiting;
        lock (_taskLock) waiting = _waitingTasks.ToList();

        foreach (var t in waiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _engine.ResumeAsync(t.Gid, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaEngineTaskService] ResumeAll failed for {t.Gid}: {ex.Message}");
            }
        }
    }

    public async Task PurgeCompletedTasksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engine.PurgeDownloadResultAsync(cancellationToken);
    }

    public async Task ApplySpeedLimitAsync(
        long downloadLimitBytes,
        long uploadLimitBytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var options = new List<KeyValuePair<string, string>>
        {
            new("max-overall-download-limit", Math.Max(0, downloadLimitBytes).ToString()),
            new("max-overall-upload-limit", Math.Max(0, uploadLimitBytes).ToString())
        };
        await _engine.ChangeGlobalOptionAsync(options, cancellationToken);
    }

    public async Task SaveAndApplySettingsAsync(
        AppSettings currentSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ThrowIfDisposed();

        var errors = currentSettings.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors), nameof(currentSettings));
        }

        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await _settingsService.SaveAsync(currentSettings);

            if (_engine.State == EngineState.Ready)
            {
                var options = new List<KeyValuePair<string, string>>
                {
                    new("max-concurrent-downloads", Math.Max(1, currentSettings.MaxConcurrentDownloads).ToString()),
                    new("max-overall-download-limit", Math.Max(0, currentSettings.MaxOverallDownloadLimit).ToString()),
                    new("max-overall-upload-limit", Math.Max(0, currentSettings.MaxOverallUploadLimit).ToString()),
                    new("split", Math.Max(1, currentSettings.Split).ToString()),
                    new("max-connection-per-server", Math.Max(1, currentSettings.Split).ToString())
                };

                if (!string.IsNullOrWhiteSpace(currentSettings.DefaultDownloadDir))
                {
                    options.Add(new KeyValuePair<string, string>("dir", currentSettings.DefaultDownloadDir));
                }

                await _engine.ChangeGlobalOptionAsync(options, cancellationToken);

                if (currentSettings.EnableBtTrackers && !string.IsNullOrWhiteSpace(currentSettings.CustomTrackersUrl))
                {
                    await UpdateTrackersAsync(cancellationToken);
                }
            }
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task UpdateTrackersAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var settings = _settingsService.Settings;
        if (!settings.EnableBtTrackers || string.IsNullOrWhiteSpace(settings.CustomTrackersUrl))
        {
            return;
        }

        var trackers = await _trackerService.FetchTrackersAsync(settings.CustomTrackersUrl, cancellationToken);
        var combinedTrackers = new HashSet<string>(trackers, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(settings.ExtraTrackers))
        {
            var extras = settings.ExtraTrackers.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                               .Select(t => t.Trim())
                                               .Where(t => !string.IsNullOrEmpty(t));
            foreach (var extra in extras)
            {
                combinedTrackers.Add(extra);
            }
        }

        if (combinedTrackers.Count > 0)
        {
            var trackerString = string.Join(",", combinedTrackers);
            await _engine.ChangeGlobalOptionAsync(new[]
            {
                new KeyValuePair<string, string>("bt-tracker", trackerString)
            }, cancellationToken);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _engine.SnapshotUpdated -= OnSnapshotUpdated;
        _engine.StateChanged -= OnStateChanged;

        _lifetimeCts.Cancel();
        if (_eventsWatchTask != null)
        {
            try { await _eventsWatchTask; } catch { }
        }

        try
        {
            await _engine.ShutdownAsync(cancellationToken);
        }
        finally
        {
            _lifetimeCts.Dispose();
            _opLock.Dispose();
            _settingsLock.Dispose();
        }
    }

    public void OpenFile(string filePath) => _fileSystemService.OpenFile(filePath);

    public void OpenDirectory(string directoryPath) => _fileSystemService.OpenDirectory(directoryPath);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AriaEngineTaskService));
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _isDisposed) == 0)
        {
            ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        GC.SuppressFinalize(this);
    }
}
