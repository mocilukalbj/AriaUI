using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using AriaUI.Helpers;
using AriaUI.Models;
using AriaUI.Services;
using AriaUI.Services.Engine;
using AriaUI.Services.Gateway;
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
        collection.AddSingleton<ISettingsService, SettingsService>();
        collection.AddSingleton<IFileSystemService, FileSystemService>();
        collection.AddSingleton<ITrackerService>(_ => new TrackerService());

#if USE_NATIVE_ENGINE
        NativeAriaEngineHost.ConfigureNativeResolution();
        collection.AddSingleton<IAriaEngine>(sp =>
        {
            var config = new EngineRuntimeConfig();
            config.Validate();
            return new NativeAriaEngineHost(config);
        });
        collection.AddSingleton<IAriaTaskService, AriaEngineTaskService>();
        collection.AddSingleton<AppGatewayService>();
#else
        collection.AddSingleton<IAriaProcessService, AriaProcessService>();
        collection.AddSingleton<IAriaRpcClient, AriaWebSocketRpcClient>();
        collection.AddSingleton<IAriaTaskService, AriaTaskService>();
#endif

        // Register Factories (Reflection-free for AOT compatibility)
        collection.AddTransient<NewTaskViewModel>();
        collection.AddSingleton<Func<NewTaskViewModel>>(sp => () =>
            new NewTaskViewModel(sp.GetRequiredService<IAriaTaskService>(), sp.GetRequiredService<ISettingsService>()));
        collection.AddSingleton<Func<AriaTaskInfo, TaskItemViewModel>>(sp =>
            taskInfo => new TaskItemViewModel(taskInfo, sp.GetRequiredService<IAriaTaskService>()));

        // Register ViewModels
        collection.AddSingleton<TaskListViewModel>();
        collection.AddSingleton<SettingsViewModel>();
        collection.AddSingleton<MainWindowViewModel>();

        Services = collection.BuildServiceProvider();

        // Initialize Settings and Theme synchronously
        var settingsService = Services.GetRequiredService<ISettingsService>();
        ApplyTheme(settingsService.Settings.ThemeMode);

        // Listen for Theme changes (marshaled to UI Thread to prevent threading issues)
        WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(this, (r, m) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyTheme(m.ThemeMode));
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var mainVm = Services.GetRequiredService<MainWindowViewModel>();
            var taskService = Services.GetRequiredService<IAriaTaskService>();
#if USE_NATIVE_ENGINE
            var gatewayService = Services.GetRequiredService<AppGatewayService>();
#else
            var processService = Services.GetRequiredService<IAriaProcessService>();
            var rpcClient = Services.GetRequiredService<IAriaRpcClient>();
#endif

            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            // Register asynchronous, non-blocking graceful shutdown pipeline
            mainWindow.RegisterAsyncShutdownHandler(async () =>
            {
                var failures = new List<Exception>();
#if USE_NATIVE_ENGINE
                try
                {
                    await gatewayService.DisposeAsync();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
#endif

                try
                {
                    await taskService.ShutdownAsync();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }

#if !USE_NATIVE_ENGINE
                try
                {
                    using var processTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await processService.StopDaemonAsync(processTimeoutCts.Token);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }

                try
                {
                    await rpcClient.DisposeAsync();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
#endif

                if (failures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(failures[0]).Throw();
                }

                if (failures.Count > 1)
                {
                    throw new AggregateException("应用关闭时发生多个错误。", failures);
                }
            });

            desktop.MainWindow = mainWindow;

            // Non-blocking cleanup fallback
            desktop.Exit += (s, e) =>
            {
                Services?.GetService<IAriaTaskService>()?.Dispose();
#if USE_NATIVE_ENGINE
                Services?.GetService<AppGatewayService>()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#else
                Services?.GetService<IAriaProcessService>()?.Dispose();
#endif
            };

            base.OnFrameworkInitializationCompleted();

#if USE_NATIVE_ENGINE
            // Start Gateway & Single-Instance Lock first, then initialize task service
            gatewayService.StartAsync().ContinueWith(gwTask =>
            {
                if (gwTask.IsFaulted)
                {
                    var ex = gwTask.Exception?.InnerException ?? gwTask.Exception;
                    if (ex is InvalidOperationException)
                    {
                        Console.Error.WriteLine($"[AriaUI Single Instance]: {ex?.Message}");
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
                        return;
                    }
                    Console.Error.WriteLine($"[App Gateway Error]: {ex}");
                }

                taskService.InitializeAsync().SafeFireAndForget(initEx =>
                {
                    Console.Error.WriteLine($"[App Initialization Error]: {initEx}");
                    WeakReferenceMessenger.Default.Send(new NotificationMessage($"初始化 Aria2 服务失败: {initEx.Message}", IsError: true));
                });
            }, TaskScheduler.Default);
#else
            // Initialize Task Service asynchronously in background with error observation
            taskService.InitializeAsync().SafeFireAndForget(ex =>
            {
                Console.Error.WriteLine($"[App Initialization Error]: {ex}");
                WeakReferenceMessenger.Default.Send(new NotificationMessage($"初始化 Aria2 服务失败: {ex.Message}", IsError: true));
            });
#endif
        }
        else
        {
            base.OnFrameworkInitializationCompleted();
        }
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
