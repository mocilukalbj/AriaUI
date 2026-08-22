using System;
using System.IO;
using System.Threading.Tasks;
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

    public bool IsActive => Status == "active";
    public bool IsPaused => Status == "paused" || Status == "waiting";
    public bool IsComplete => Status == "complete";
    public bool IsError => Status == "error";

    public bool CanPause => IsActive;
    public bool CanResume => IsPaused;
    public bool CanOpenFile => IsComplete && !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(FilePath);

    public TaskItemViewModel(AriaTaskInfo taskInfo, IAriaTaskService taskService)
    {
        _taskService = taskService;
        Update(taskInfo);
    }

    public void Update(AriaTaskInfo info)
    {
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
        ErrorMessage = info.ErrorMessage;

        if (int.TryParse(info.Connections, out var conn))
        {
            ConnectionCount = conn;
        }

        SpeedText = FormatHelper.FormatSpeed(DownloadSpeedBytes);
        UploadSpeedText = FormatHelper.FormatSpeed(UploadSpeedBytes);
        SizeText = $"{FormatHelper.FormatBytes(CompletedBytes)} / {FormatHelper.FormatBytes(TotalBytes)}";
        EtaText = FormatHelper.FormatEta(TotalBytes, CompletedBytes, DownloadSpeedBytes);

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanOpenFile));
        OnPropertyChanged(nameof(CanOpenFolder));
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
        _taskService.OpenFile(FilePath);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        _taskService.OpenDirectory(FilePath);
    }
}
