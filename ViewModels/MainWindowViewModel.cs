using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AriaUI.Helpers;
using AriaUI.Models;
using AriaUI.Services;

namespace AriaUI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase,
    IRecipient<GlobalStatUpdatedMessage>,
    IRecipient<NotificationMessage>
{
    private readonly IAriaTaskService _taskService;
    private readonly ISettingsService _settingsService;
    private readonly IAriaProcessService _processService;

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private int _selectedNavigationIndex = 0; // 0 = Tasks, 1 = Settings

    [ObservableProperty]
    private string _downloadSpeedText = "0 B/s";

    [ObservableProperty]
    private string _uploadSpeedText = "0 B/s";

    [ObservableProperty]
    private string _connectionStatusText = "未连接";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _hasActiveTasks;

    [ObservableProperty]
    private string? _toastMessage;

    [ObservableProperty]
    private bool _showToast;

    [ObservableProperty]
    private bool _isToastError;

    public TaskListViewModel TaskListVm { get; }
    public SettingsViewModel SettingsVm { get; }

    public MainWindowViewModel(
        IAriaTaskService taskService,
        ISettingsService settingsService,
        IAriaProcessService processService,
        TaskListViewModel taskListVm,
        SettingsViewModel settingsVm)
    {
        _taskService = taskService;
        _settingsService = settingsService;
        _processService = processService;

        TaskListVm = taskListVm;
        SettingsVm = settingsVm;

        _currentView = TaskListVm;

        WeakReferenceMessenger.Default.Register<GlobalStatUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<NotificationMessage>(this);
    }

    public void Receive(GlobalStatUpdatedMessage message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var stat = message.Stat;
            DownloadSpeedText = FormatHelper.FormatSpeed(stat.DownloadSpeedBytes);
            UploadSpeedText = FormatHelper.FormatSpeed(stat.UploadSpeedBytes);
            IsConnected = message.IsConnected;
            ConnectionStatusText = IsConnected ? "Aria2 已连接" : "Aria2 连接断开";
            HasActiveTasks = TaskListVm.ActiveTaskCount > 0;
        });
    }

    public void Receive(NotificationMessage message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowNotification(message.Message, message.IsError));
    }

    partial void OnSelectedNavigationIndexChanged(int value)
    {
        CurrentView = value switch
        {
            1 => SettingsVm,
            _ => TaskListVm
        };
    }

    public void ShowNotification(string message, bool isError = false)
    {
        ToastMessage = message;
        IsToastError = isError;
        ShowToast = true;

        Task.Delay(3000).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowToast = false);
        });
    }

    [RelayCommand]
    private void NavigateToTasks()
    {
        SelectedNavigationIndex = 0;
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        SelectedNavigationIndex = 1;
    }
}
