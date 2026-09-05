using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AriaUI.Host;

/// <summary>
/// Native Messaging Thin Host (§8.2–§8.3, G01–G12).
/// Bridges Chromium stdio Native Messaging protocol to the AriaUI Unix Domain Socket gateway.
/// Stdout contains ONLY 32-bit framed protocol messages. All logs go to stderr.
/// </summary>
public static class Program
{
    private const int MaxFrameSize = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> Main(string[] args)
    {
        Console.Error.WriteLine("[AriaUI.Host] Native messaging host started.");

        string socketPath = ResolveSocketPath();
        string lockFilePath = ResolveLockPath();

        // Ensure App is reachable (cold start if necessary)
        Socket? socket = null;
        try
        {
            socket = await EnsureConnectedAsync(socketPath, lockFilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaUI.Host] Failed to connect to AriaUI gateway: {ex.Message}");
            return 1;
        }

        using (socket)
        using (var socketStream = new NetworkStream(socket, ownsSocket: false))
        using (var stdIn = Console.OpenStandardInput())
        using (var stdOut = Console.OpenStandardOutput())
        {
            byte[] lenBuf = new byte[4];

            // Bridge loop: read stdio -> forward to socket -> read response from socket -> write to stdout
            while (true)
            {
                int read = await ReadExactAsync(stdIn, lenBuf, 4, CancellationToken.None);
                if (read < 4)
                {
                    Console.Error.WriteLine("[AriaUI.Host] Stdin reached EOF. Terminating.");
                    break;
                }

                uint frameLen = BitConverter.ToUInt32(lenBuf, 0);
                if (frameLen == 0 || frameLen > MaxFrameSize)
                {
                    Console.Error.WriteLine($"[AriaUI.Host] Received invalid frame length from browser: {frameLen} bytes.");
                    break;
                }

                byte[] payload = new byte[frameLen];
                int payloadRead = await ReadExactAsync(stdIn, payload, (int)frameLen, CancellationToken.None);
                if (payloadRead < (int)frameLen)
                {
                    Console.Error.WriteLine("[AriaUI.Host] Incomplete frame payload on stdin.");
                    break;
                }

                // Send frame to Unix socket
                await socketStream.WriteAsync(lenBuf, 0, 4);
                await socketStream.WriteAsync(payload, 0, (int)frameLen);
                await socketStream.FlushAsync();

                // Read response from Unix socket
                byte[] respLenBuf = new byte[4];
                int respRead = await ReadExactAsync(socketStream, respLenBuf, 4, CancellationToken.None);
                if (respRead < 4)
                {
                    Console.Error.WriteLine("[AriaUI.Host] Gateway socket closed unexpectedly.");
                    break;
                }

                uint respFrameLen = BitConverter.ToUInt32(respLenBuf, 0);
                if (respFrameLen == 0 || respFrameLen > MaxFrameSize)
                {
                    Console.Error.WriteLine($"[AriaUI.Host] Gateway returned invalid frame length: {respFrameLen} bytes.");
                    break;
                }

                byte[] respPayload = new byte[respFrameLen];
                int respPayloadRead = await ReadExactAsync(socketStream, respPayload, (int)respFrameLen, CancellationToken.None);
                if (respPayloadRead < (int)respFrameLen)
                {
                    Console.Error.WriteLine("[AriaUI.Host] Gateway returned incomplete payload.");
                    break;
                }

                // Write response to stdout for Chromium
                await stdOut.WriteAsync(respLenBuf, 0, 4);
                await stdOut.WriteAsync(respPayload, 0, (int)respFrameLen);
                await stdOut.FlushAsync();
            }
        }

        Console.Error.WriteLine("[AriaUI.Host] Exiting cleanly.");
        return 0;
    }

    private static string ResolveSocketPath()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir))
        {
            return Path.Combine(runtimeDir, "ariaui", "gateway.sock");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ariaui", "gateway.sock");
    }

    private static string ResolveLockPath()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir))
        {
            return Path.Combine(runtimeDir, "ariaui", "ariaui.lock");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ariaui", "ariaui.lock");
    }

    private static async Task<Socket> EnsureConnectedAsync(string socketPath, string lockFilePath)
    {
        var deadline = DateTime.UtcNow + ConnectTimeout;
        bool coldStartAttempted = false;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(socketPath))
            {
                try
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
                    return socket;
                }
                catch (SocketException)
                {
                    // Socket might not be ready yet
                }
            }

            if (!coldStartAttempted)
            {
                coldStartAttempted = true;
                bool lockHeld = false;
                try
                {
                    using (var fs = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        // Lock not held
                    }
                }
                catch (IOException)
                {
                    lockHeld = true; // App is already starting or running
                }

                if (!lockHeld)
                {
                    Console.Error.WriteLine("[AriaUI.Host] AriaUI is not running. Initiating cold start...");
                    TryLaunchApp();
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Failed to connect to AriaUI gateway at {socketPath} within {ConnectTimeout.TotalSeconds} seconds.");
    }

    public static string? FindAppBinary()
    {
        var envPath = Environment.GetEnvironmentVariable("ARIAUI_APP_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var hostDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(hostDir, "AriaUI"),
            Path.Combine(hostDir, "ariaui"),
            "/usr/bin/ariaui",
            "/usr/local/bin/ariaui",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "ariaui")
        };

        // Walk up directory tree to find build outputs in parent project folders
        var dir = new DirectoryInfo(hostDir);
        for (int i = 0; i < 7 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "bin", "Debug", "net10.0", "AriaUI"));
            candidates.Add(Path.Combine(dir.FullName, "bin", "Release", "net10.0", "AriaUI"));
            candidates.Add(Path.Combine(dir.FullName, "bin", "Release", "net10.0", "linux-x64", "publish", "AriaUI"));
            candidates.Add(Path.Combine(dir.FullName, "AriaUI"));
            dir = dir.Parent;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch { }
        }

        return null;
    }

    private static void TryLaunchApp()
    {
        var binary = FindAppBinary();
        if (binary != null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = binary,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                Console.Error.WriteLine($"[AriaUI.Host] Cold start launched: {binary}");
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaUI.Host] Cold start launch failed: {ex.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine("[AriaUI.Host] Cold start executable not found in candidate paths.");
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int r = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (r == 0) break;
            total += r;
        }
        return total;
    }
}
