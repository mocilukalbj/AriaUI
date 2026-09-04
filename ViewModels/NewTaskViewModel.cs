using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AriaUI.Services;
using System.Threading;

namespace AriaUI.ViewModels;

public partial class NewTaskViewModel : ViewModelBase
{
    private readonly IAriaTaskService _taskService;
    private readonly ISettingsService _settingsService;
    private CancellationTokenSource? _submitCts;

    [ObservableProperty]
    private int _selectedTabIndex; // 0 = URL/Magnet, 1 = Torrent

    [ObservableProperty]
    private string _urls = string.Empty;

    [ObservableProperty]
    private string _torrentPath = string.Empty;

    [ObservableProperty]
    private string _saveDir = string.Empty;

    [ObservableProperty]
    private decimal _split = 16;

    [ObservableProperty]
    private string _referer = string.Empty;

    [ObservableProperty]
    private string _userAgent = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isSubmitting;

    public event Action? RequestClose;

    public NewTaskViewModel(IAriaTaskService taskService, ISettingsService settingsService)
    {
        _taskService = taskService;
        _settingsService = settingsService;
        SaveDir = _settingsService.Settings.DefaultDownloadDir;
        Split = _settingsService.Settings.Split;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = null;
        IsSubmitting = true;
        using var submitCts = new CancellationTokenSource();
        _submitCts = submitCts;

        try
        {
            if (SelectedTabIndex == 0)
            {
                if (string.IsNullOrWhiteSpace(Urls))
                {
                    ErrorMessage = "请输入下载链接或磁力链接 (Magnet)";
                    return;
                }

                await _taskService.AddUriAsync(
                    Urls,
                    SaveDir,
                    (int)Split,
                    Referer,
                    UserAgent,
                    submitCts.Token);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(TorrentPath) || !File.Exists(TorrentPath))
                {
                    ErrorMessage = "请选择有效的 .torrent 种子文件";
                    return;
                }

                await _taskService.AddTorrentAsync(TorrentPath, SaveDir, submitCts.Token);
            }

            submitCts.Token.ThrowIfCancellationRequested();
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) when (submitCts.IsCancellationRequested)
        {
            // Cancel closes the dialog; no stale error should be shown afterwards.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"添加任务失败: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_submitCts, submitCts))
            {
                _submitCts = null;
            }
            IsSubmitting = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _submitCts?.Cancel();
        RequestClose?.Invoke();
    }
}
