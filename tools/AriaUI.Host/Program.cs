using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using AriaUI.Helpers;
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
        Stream? gatewayStream = null;
        try
        {
            gatewayStream = await EnsureConnectedAsync(socketPath, lockFilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaUI.Host] Failed to connect to AriaUI gateway: {ex.Message}");
            return 1;
        }

        using (var socketStream = gatewayStream)
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

    private static string ResolveSocketPath() => LocalGatewayEndpoint.SocketPath;
    private static string ResolveLockPath() => LocalGatewayEndpoint.LockPath;

    private static async Task<Stream> EnsureConnectedAsync(string socketPath, string lockFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockFilePath)!);
        var deadline = DateTime.UtcNow + ConnectTimeout;
        bool coldStartAttempted = false;

        while (DateTime.UtcNow < deadline)
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", LocalGatewayEndpoint.PipeNameForLock(lockFilePath),
                    PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(250);
                    return pipe;
                }
                catch (TimeoutException) { pipe.Dispose(); }
                catch (IOException) { pipe.Dispose(); }
                catch { pipe.Dispose(); throw; }
            }
            else if (File.Exists(socketPath))
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch (OperationCanceledException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
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
        var executable = OperatingSystem.IsWindows() ? "AriaUI.exe" : "AriaUI";
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var candidates = new List<string>
        {
            Path.Combine(hostDir, executable),
            Path.Combine(hostDir, "ariaui"),
            "/usr/bin/ariaui",
            "/usr/local/bin/ariaui",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "ariaui")
        };

        // Walk up directory tree to find build outputs in parent project folders
        var dir = new DirectoryInfo(hostDir);
        for (int i = 0; i < 7 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "bin", "Debug", "net10.0", executable));
            candidates.Add(Path.Combine(dir.FullName, "bin", "Release", "net10.0", executable));
            candidates.Add(Path.Combine(dir.FullName, "bin", "Release", "net10.0", rid, "publish", executable));
            candidates.Add(Path.Combine(dir.FullName, executable));
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
                    // Detach the Windows GUI: its logs must never inherit Native Messaging stdout.
                    UseShellExecute = OperatingSystem.IsWindows(),
                    CreateNoWindow = true
                };
                using var launched = Process.Start(psi);
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
