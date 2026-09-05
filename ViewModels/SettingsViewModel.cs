using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AriaUI.Models;
using AriaUI.Services;

namespace AriaUI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IAriaTaskService _taskService;

    [ObservableProperty]
    private string _defaultDownloadDir = string.Empty;

    [ObservableProperty]
    private decimal _maxConcurrentDownloads = 5;

    [ObservableProperty]
    private decimal _maxConnectionPerServer = 16;

    [ObservableProperty]
    private decimal _split = 16;

    [ObservableProperty]
    private decimal _downloadLimitKB;

    [ObservableProperty]
    private decimal _uploadLimitKB;

    [ObservableProperty]
    private bool _enableBtTrackers = true;

    [ObservableProperty]
    private string _customTrackersUrl = string.Empty;

    [ObservableProperty]
    private string _extraTrackers = string.Empty;

    [ObservableProperty]
    private bool _allowInvalidCert;

    [ObservableProperty]
    private string _themeMode = "System";

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isUpdatingTrackers;

    public List<string> AvailableThemes { get; } = new() { "System", "Light", "Dark" };

    public SettingsViewModel(
        ISettingsService settingsService,
        IAriaTaskService taskService)
    {
        _settingsService = settingsService;
        _taskService = taskService;

        LoadFromSettings();
    }

    public void LoadFromSettings()
    {
        var s = _settingsService.Settings;
        DefaultDownloadDir = s.DefaultDownloadDir;
        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        MaxConnectionPerServer = s.MaxConnectionPerServer;
        Split = s.Split;
        DownloadLimitKB = s.MaxOverallDownloadLimit / 1024;
        UploadLimitKB = s.MaxOverallUploadLimit / 1024;
        EnableBtTrackers = s.EnableBtTrackers;
        CustomTrackersUrl = s.CustomTrackersUrl;
        ExtraTrackers = s.ExtraTrackers;
        AllowInvalidCert = s.AllowInvalidCert;
        ThemeMode = s.ThemeMode;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        StatusMessage = null;
        IsStatusError = false;

        try
        {
            var newSettings = new AppSettings
            {
                DefaultDownloadDir = DefaultDownloadDir,
                MaxConcurrentDownloads = (int)MaxConcurrentDownloads,
                MaxConnectionPerServer = (int)MaxConnectionPerServer,
                Split = (int)Split,
                MaxOverallDownloadLimit = (long)DownloadLimitKB * 1024,
                MaxOverallUploadLimit = (long)UploadLimitKB * 1024,
                EnableBtTrackers = EnableBtTrackers,
                CustomTrackersUrl = CustomTrackersUrl,
                ExtraTrackers = ExtraTrackers,
                AllowInvalidCert = AllowInvalidCert,
                ThemeMode = ThemeMode
            };

            var errors = newSettings.Validate();
            if (errors.Count > 0)
            {
                StatusMessage = $"保存失败: {string.Join("; ", errors)}";
                IsStatusError = true;
                return;
            }

            await _taskService.SaveAndApplySettingsAsync(newSettings);
            LoadFromSettings();
            WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(ThemeMode));

            WeakReferenceMessenger.Default.Send(new NotificationMessage("配置已保存并生效！"));

            StatusMessage = "设置已保存并生效！";
            IsStatusError = false;
        }
        catch (SettingsApplicationException ex)
        {
            LoadFromSettings();
            WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(ThemeMode));
            StatusMessage = $"{ex.Message} {ex.InnerException?.Message}";
            IsStatusError = true;
            WeakReferenceMessenger.Default.Send(
                new NotificationMessage(StatusMessage, IsError: true));
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存配置失败: {ex.Message}";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private async Task UpdateTrackersAsync()
    {
        IsUpdatingTrackers = true;
        StatusMessage = "正在更新 Tracker 列表...";
        IsStatusError = false;

        try
        {
            await _taskService.UpdateTrackersAsync();
            ExtraTrackers = _settingsService.Settings.ExtraTrackers;
            StatusMessage = "Tracker 列表更新成功！";
            IsStatusError = false;
        }
        catch (SettingsApplicationException ex)
        {
            ExtraTrackers = _settingsService.Settings.ExtraTrackers;
            StatusMessage = $"{ex.Message} {ex.InnerException?.Message}";
            IsStatusError = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"更新 Tracker 失败: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            IsUpdatingTrackers = false;
        }
    }
}
