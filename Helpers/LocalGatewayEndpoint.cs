using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AriaUI.Helpers;

// Shared source with the thin host: endpoint discovery must agree in both processes.
public static class LocalGatewayEndpoint
{
    public static string DataDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("ARIAUI_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AriaUI")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ariaui");
        }
    }

    public static string RuntimeDirectory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)
                ? Path.Combine(runtime, "ariaui") : DataDirectory;
        }
    }

    public static string SocketPath => Resolve("ARIAUI_SOCKET_PATH", "gateway.sock");
    public static string LockPath => Resolve("ARIAUI_LOCK_PATH", "ariaui.lock");

    private static string Resolve(string variable, string name)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? Path.Combine(RuntimeDirectory, name) : Path.GetFullPath(value);
    }

    public static string PipeNameForLock(string lockPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user SID unavailable.");
        var identity = user + "\n" + Path.GetFullPath(lockPath).ToUpperInvariant();
        return "ariaui-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }
}
