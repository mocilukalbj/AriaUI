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

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[APP RUN CRASH]: {ex}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
