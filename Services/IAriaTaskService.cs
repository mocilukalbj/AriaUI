using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    Task<string> AddUriAsync(
        string url,
        string? saveDir,
        int? split,
        string? referer,
        string? userAgent,
        CancellationToken cancellationToken);
    Task<string> AddUriAsync(
        string url,
        string? saveDir = null,
        int? split = null,
        string? referer = null,
        string? userAgent = null,
        IReadOnlyList<string>? headers = null,
        string? outFilename = null,
        CancellationToken cancellationToken = default);
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
