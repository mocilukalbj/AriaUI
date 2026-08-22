using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AriaUI.Models;
using AriaUI.Services;

namespace AriaUI.ViewModels;

public partial class TaskListViewModel : ViewModelBase, IRecipient<TasksUpdatedMessage>
{
    private readonly IAriaTaskService _taskService;
    private readonly ISettingsService _settingsService;
    private readonly Func<AriaTaskInfo, TaskItemViewModel> _taskItemFactory;
    private readonly Func<NewTaskViewModel> _newTaskFactory;
    private readonly Dictionary<string, TaskItemViewModel> _taskMap = new();

    [ObservableProperty]
    private int _filterIndex = 0; // 0=All, 1=Active, 2=Waiting, 3=Complete, 4=Stopped

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _totalTaskCount;

    [ObservableProperty]
    private int _activeTaskCount;

    [ObservableProperty]
    private int _waitingTaskCount;

    [ObservableProperty]
    private int _completeTaskCount;

    [ObservableProperty]
    private int _stoppedTaskCount;

    [ObservableProperty]
    private bool _hasTasks;

    [ObservableProperty]
    private bool _hasNoTasks = true;

    [ObservableProperty]
    private bool _hasActiveTasks;

    [ObservableProperty]
    private bool _showNewTaskDialog;

    [ObservableProperty]
    private NewTaskViewModel? _newTaskVm;

    public ObservableCollection<TaskItemViewModel> FilteredTasks { get; } = new();

    public TaskListViewModel(
        IAriaTaskService taskService,
        ISettingsService settingsService,
        Func<AriaTaskInfo, TaskItemViewModel> taskItemFactory,
        Func<NewTaskViewModel> newTaskFactory)
    {
        _taskService = taskService;
        _settingsService = settingsService;
        _taskItemFactory = taskItemFactory;
        _newTaskFactory = newTaskFactory;

        WeakReferenceMessenger.Default.Register<TasksUpdatedMessage>(this);
    }

    public void Receive(TasksUpdatedMessage message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTasksFromService);
    }

    partial void OnFilterIndexChanged(int value)
    {
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void UpdateTasksFromService()
    {
        try
        {
            var allServiceTasks = new List<AriaTaskInfo>();
            if (_taskService.ActiveTasks != null) allServiceTasks.AddRange(_taskService.ActiveTasks);
            if (_taskService.WaitingTasks != null) allServiceTasks.AddRange(_taskService.WaitingTasks);
            if (_taskService.StoppedTasks != null) allServiceTasks.AddRange(_taskService.StoppedTasks);

            var existingGids = new HashSet<string>(_taskMap.Keys);
            var currentGids = new HashSet<string>();

            int active = 0, waiting = 0, complete = 0, stopped = 0;

            foreach (var taskInfo in allServiceTasks)
            {
                if (string.IsNullOrEmpty(taskInfo.Gid)) continue;
                currentGids.Add(taskInfo.Gid);

                switch (taskInfo.Status.ToLowerInvariant())
                {
                    case "active": active++; break;
                    case "waiting":
                    case "paused": waiting++; break;
                    case "complete": complete++; break;
                    default: stopped++; break;
                }

                if (_taskMap.TryGetValue(taskInfo.Gid, out var vm))
                {
                    vm.Update(taskInfo);
                }
                else
                {
                    var newVm = _taskItemFactory(taskInfo);
                    _taskMap[taskInfo.Gid] = newVm;
                }
            }

            // Remove deleted tasks
            foreach (var gid in existingGids)
            {
                if (!currentGids.Contains(gid))
                {
                    _taskMap.Remove(gid);
                }
            }

            ActiveTaskCount = active;
            WaitingTaskCount = waiting;
            CompleteTaskCount = complete;
            StoppedTaskCount = stopped;
            TotalTaskCount = allServiceTasks.Count;
            HasActiveTasks = active > 0;

            ApplyFilter();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TaskListViewModel] Error updating tasks: {ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        try
        {
            var query = _taskMap.Values.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = query.Where(t => t.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            }

            query = FilterIndex switch
            {
                1 => query.Where(t => t.IsActive),
                2 => query.Where(t => t.IsPaused),
                3 => query.Where(t => t.IsComplete),
                4 => query.Where(t => t.IsError || (!t.IsActive && !t.IsPaused && !t.IsComplete)),
                _ => query
            };

            var newList = query.ToList();

            // Incremental sync to prevent UI flickering and maintain scroll state
            int i = 0;
            for (; i < newList.Count; i++)
            {
                if (i < FilteredTasks.Count)
                {
                    if (!ReferenceEquals(FilteredTasks[i], newList[i]))
                    {
                        FilteredTasks[i] = newList[i];
                    }
                }
                else
                {
                    FilteredTasks.Add(newList[i]);
                }
            }

            while (FilteredTasks.Count > newList.Count)
            {
                FilteredTasks.RemoveAt(FilteredTasks.Count - 1);
            }

            HasTasks = FilteredTasks.Count > 0;
            HasNoTasks = FilteredTasks.Count == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TaskListViewModel] Error applying filter: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenNewTaskDialog()
    {
        var vm = _newTaskFactory();
        vm.RequestClose += () =>
        {
            ShowNewTaskDialog = false;
            NewTaskVm = null;
        };
        NewTaskVm = vm;
        ShowNewTaskDialog = true;
    }

    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        try
        {
            await _taskService.ResumeAllTasksAsync();
            WeakReferenceMessenger.Default.Send(new NotificationMessage("已开始所有下载任务"));
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"全部开始失败: {ex.Message}", IsError: true));
        }
    }

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        try
        {
            await _taskService.PauseAllTasksAsync();
            WeakReferenceMessenger.Default.Send(new NotificationMessage("已暂停所有下载任务"));
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"全部暂停失败: {ex.Message}", IsError: true));
        }
    }

    [RelayCommand]
    private async Task PurgeCompletedAsync()
    {
        try
        {
            await _taskService.PurgeCompletedTasksAsync();
            WeakReferenceMessenger.Default.Send(new NotificationMessage("已清理所有已完成和已停止的下载记录"));
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new NotificationMessage($"清理记录失败: {ex.Message}", IsError: true));
        }
    }

    [RelayCommand]
    private void SetFilter(string filter)
    {
        if (int.TryParse(filter, out var idx))
        {
            FilterIndex = idx;
        }
    }
}
