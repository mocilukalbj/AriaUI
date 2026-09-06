using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AriaUI.Helpers;
using AriaUI.Models;
using AriaUI.Services;

namespace AriaUI.ViewModels;

public partial class TaskItemViewModel : ViewModelBase
{
    private readonly IAriaTaskService _taskService;
    private int _fileCheckVersion;
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private string _gid = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _completedBytes;

    [ObservableProperty]
    private long _downloadSpeedBytes;

    [ObservableProperty]
    private long _uploadSpeedBytes;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _speedText = "0 B/s";

    [ObservableProperty]
    private string _uploadSpeedText = "0 B/s";

    [ObservableProperty]
    private string _sizeText = "0 B / 0 B";

    [ObservableProperty]
    private string _etaText = "--";

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _connectionCount;

    [ObservableProperty]
    private bool _hasUploadSpeed;

    [ObservableProperty]
    private bool _canOpenFile;

    public bool IsActive => Status == "active";
    public bool IsPaused => Status == "paused" || Status == "waiting";
    public bool IsComplete => Status == "complete";
    public bool IsError => Status == "error";

    public bool CanPause => IsActive;
    public bool CanResume => IsPaused;
    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(FilePath);

    public TaskItemViewModel(AriaTaskInfo taskInfo, IAriaTaskService taskService)
    {
        _taskService = taskService;
        Update(taskInfo);
    }

    public void Update(AriaTaskInfo info)
    {
        var wasComplete = IsComplete;
        var previousFilePath = FilePath;

        Gid = info.Gid;
        Name = info.DisplayName;
        Status = info.Status;
        TotalBytes = info.TotalBytes;
        CompletedBytes = info.CompletedBytes;
        DownloadSpeedBytes = info.DownloadSpeedBytes;
        UploadSpeedBytes = info.UploadSpeedBytes;
        HasUploadSpeed = UploadSpeedBytes > 0;
        Progress = info.ProgressPercentage;
        FilePath = info.PrimaryFilePath;
        _folderPath = info.Files is { Count: > 0 } && !string.IsNullOrWhiteSpace(info.Files[0].Path)
            ? Path.GetDirectoryName(info.Files[0].Path) ?? info.Dir ?? string.Empty
            : info.Dir ?? string.Empty;
        ErrorMessage = info.ErrorMessage;

        if (int.TryParse(info.Connections, out var conn))
        {
            ConnectionCount = conn;
        }

        SpeedText = FormatHelper.FormatSpeed(DownloadSpeedBytes);
        UploadSpeedText = FormatHelper.FormatSpeed(UploadSpeedBytes);
        SizeText = $"{FormatHelper.FormatBytes(CompletedBytes)} / {FormatHelper.FormatBytes(TotalBytes)}";
        EtaText = FormatHelper.FormatEta(TotalBytes, CompletedBytes, DownloadSpeedBytes);

        if (!IsComplete || string.IsNullOrWhiteSpace(FilePath))
        {
            Interlocked.Increment(ref _fileCheckVersion);
            CanOpenFile = false;
        }
        else if (!wasComplete || !string.Equals(previousFilePath, FilePath, StringComparison.Ordinal))
        {
            var version = Interlocked.Increment(ref _fileCheckVersion);
            RefreshCanOpenFileAsync(FilePath, version).SafeFireAndForget();
        }

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanOpenFolder));
    }

    private async Task RefreshCanOpenFileAsync(string filePath, int version)
    {
        var exists = await Task.Run(() => File.Exists(filePath)).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (version == Volatile.Read(ref _fileCheckVersion) &&
                IsComplete &&
                string.Equals(FilePath, filePath, StringComparison.Ordinal))
            {
                CanOpenFile = exists;
            }
        });
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        try
        {
            await _taskService.PauseTaskAsync(Gid);
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"暂停任务失败: {ex.Message}", IsError: true));
        }
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        try
        {
            await _taskService.ResumeTaskAsync(Gid);
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"恢复任务失败: {ex.Message}", IsError: true));
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(object? deleteFileParam)
    {
        bool deleteFile = false;
        if (deleteFileParam is bool b) deleteFile = b;
        else if (deleteFileParam is string s && bool.TryParse(s, out var parsed)) deleteFile = parsed;

        try
        {
            await _taskService.RemoveTaskAsync(Gid, deleteFile);
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"任务已移除"));
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"删除任务失败: {ex.Message}", IsError: true));
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        try
        {
            _taskService.OpenFile(FilePath);
        }
        catch (Exception ex) when (IsFileOperationError(ex))
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"打开文件失败: {ex.Message}", IsError: true));
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            _taskService.OpenDirectory(_folderPath);
        }
        catch (Exception ex) when (IsFileOperationError(ex))
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"打开目录失败: {ex.Message}", IsError: true));
        }
    }

    private static bool IsFileOperationError(Exception ex) => ex is
        IOException or UnauthorizedAccessException or ArgumentException or
        System.ComponentModel.Win32Exception or InvalidOperationException;
}
