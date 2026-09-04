using Avalonia;
using System;

namespace AriaUI;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.Error.WriteLine($"[FATAL UNHANDLED EXCEPTION]: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Console.Error.WriteLine($"[UNOBSERVED TASK EXCEPTION]: {e.Exception}");
            e.SetObserved();
        };

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
