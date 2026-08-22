using System;
using System.Threading.Tasks;

namespace AriaUI.Helpers;

public static class TaskExtensions
{
    public static async void SafeFireAndForget(
        this Task task,
        Action<Exception>? onError = null)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            Console.Error.WriteLine($"[SafeFireAndForget Exception]: {ex.Message}");
        }
    }
}
