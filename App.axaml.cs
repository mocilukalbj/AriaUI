using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using AriaUI.Models;
using AriaUI.Services;
using AriaUI.ViewModels;
using AriaUI.Views;

namespace AriaUI;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var collection = new ServiceCollection();

        // Register Core Services
        collection.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        collection.AddSingleton<ISettingsService, SettingsService>();
        collection.AddSingleton<IFileSystemService, FileSystemService>();
        collection.AddSingleton<ITrackerService, TrackerService>();
        collection.AddSingleton<IAriaProcessService, AriaProcessService>();
        collection.AddSingleton<IAriaRpcClient, AriaWebSocketRpcClient>();
        collection.AddSingleton<IAriaTaskService, AriaTaskService>();

        // Register Factories
        collection.AddTransient<NewTaskViewModel>();
        collection.AddSingleton<Func<NewTaskViewModel>>(sp => () => ActivatorUtilities.CreateInstance<NewTaskViewModel>(sp));
        collection.AddSingleton<Func<AriaTaskInfo, TaskItemViewModel>>(sp =>
            taskInfo => ActivatorUtilities.CreateInstance<TaskItemViewModel>(sp, taskInfo));

        // Register ViewModels
        collection.AddSingleton<TaskListViewModel>();
        collection.AddSingleton<SettingsViewModel>();
        collection.AddSingleton<MainWindowViewModel>();

        Services = collection.BuildServiceProvider();

        // Initialize Settings and Theme synchronously
        var settingsService = Services.GetRequiredService<ISettingsService>();
        ApplyTheme(settingsService.Settings.ThemeMode);

        // Listen for Theme changes
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(this, (r, m) =>
        {
            ApplyTheme(m.ThemeMode);
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainVm = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            var taskService = Services.GetRequiredService<IAriaTaskService>();

            // Setup System Tray Icon
            SetupTrayIcon(desktop, taskService);

            // Lifecycle clean exit
            desktop.Exit += (s, e) =>
            {
                try
                {
                    var taskServiceInstance = Services.GetService<IAriaTaskService>();
                    taskServiceInstance?.Dispose();

                    var processService = Services.GetService<IAriaProcessService>();
                    processService?.StopDaemonAsync().GetAwaiter().GetResult();

                    var rpcClient = Services.GetService<IAriaRpcClient>();
                    rpcClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[App Exit Cleanup Error]: {ex.Message}");
                }
            };

            base.OnFrameworkInitializationCompleted();

            // Initialize Task Service asynchronously in background
            _ = taskService.InitializeAsync();
        }
        else
        {
            base.OnFrameworkInitializationCompleted();
        }
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, IAriaTaskService taskService)
    {
        try
        {
            var trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AriaUI/Assets/avalonia-logo.ico"))),
                ToolTipText = "AriaUI - 下载管理器",
                IsVisible = true
            };

            var menu = new NativeMenu();

            var showItem = new NativeMenuItem("显示主窗口");
            showItem.Click += (s, e) => ShowMainWindow(desktop);

            var resumeAllItem = new NativeMenuItem("▶ 全部开始");
            resumeAllItem.Click += async (s, e) =>
            {
                try { await taskService.ResumeAllTasksAsync(); } catch { }
            };

            var pauseAllItem = new NativeMenuItem("⏸ 全部暂停");
            pauseAllItem.Click += async (s, e) =>
            {
                try { await taskService.PauseAllTasksAsync(); } catch { }
            };

            var exitItem = new NativeMenuItem("✕ 退出程序");
            exitItem.Click += (s, e) => ExitApplication(desktop);

            menu.Items.Add(showItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(resumeAllItem);
            menu.Items.Add(pauseAllItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            trayIcon.Menu = menu;
            trayIcon.Clicked += (s, e) => ShowMainWindow(desktop);

            var trayIcons = new TrayIcons { trayIcon };
            TrayIcon.SetIcons(this, trayIcons);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SetupTrayIcon Error]: {ex.Message}");
        }
    }

    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is MainWindow window)
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
        }
    }

    private void ExitApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is MainWindow window)
        {
            window.IsExplicitExit = true;
            window.Close();
        }
        desktop.Shutdown();
    }

    public void ApplyTheme(string themeMode)
    {
        RequestedThemeVariant = themeMode?.ToLowerInvariant() switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }
}