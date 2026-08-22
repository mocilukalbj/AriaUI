using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;

namespace AriaUI.Services;

public interface IAriaProcessService
{
    bool IsRunning { get; }
    string? ExecutablePath { get; }
    Task<bool> StartDaemonAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task StopDaemonAsync(CancellationToken cancellationToken = default);
}

public class AriaProcessService : IAriaProcessService
{
    private Process? _process;
    private readonly string _sessionFilePath;
    private readonly string _dhtFilePath;
    private readonly string _pidFilePath;

    public bool IsRunning => _process != null && !_process.HasExited;
    public string? ExecutablePath { get; private set; }

    public AriaProcessService()
    {
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "AriaUI");
        Directory.CreateDirectory(configDir);
        _sessionFilePath = Path.Combine(configDir, "aria2.session");
        _dhtFilePath = Path.Combine(configDir, "dht.dat");
        _pidFilePath = Path.Combine(configDir, "aria2.pid");

        if (!File.Exists(_sessionFilePath))
        {
            File.Create(_sessionFilePath).Dispose();
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            StopDaemonAsync().GetAwaiter().GetResult();
        };
    }

    public string? FindAria2Executable(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        var localBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "aria2c");
        if (File.Exists(localBin))
        {
            return localBin;
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            var full = Path.Combine(p, "aria2c");
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private void CleanupOwnStaleProcess()
    {
        if (!File.Exists(_pidFilePath)) return;

        try
        {
            var pidStr = File.ReadAllText(_pidFilePath).Trim();
            if (int.TryParse(pidStr, out var pid))
            {
                var process = Process.GetProcessById(pid);
                if (process.ProcessName.Contains("aria2c", StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(true);
                    process.WaitForExit(1000);
                }
            }
        }
        catch (ArgumentException)
        {
            // Process already exited
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaProcessService] Failed to clean up stale process: {ex.Message}");
        }
        finally
        {
            try { File.Delete(_pidFilePath); } catch { }
        }
    }

    private async Task<bool> IsPortInUseAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await client.ConnectAsync("127.0.0.1", port, linkedCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartDaemonAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return true;
        }

        // If port is already in use (e.g. external standalone daemon or existing session), connect to it
        if (await IsPortInUseAsync(settings.RpcPort, cancellationToken))
        {
            Console.WriteLine($"[AriaProcessService] Port {settings.RpcPort} is already in use, connecting to existing instance.");
            return true;
        }

        // Only cleanup stale process if port is not responding
        CleanupOwnStaleProcess();

        ExecutablePath = FindAria2Executable(settings.Aria2ExecutablePath);
        if (ExecutablePath == null)
        {
            Console.Error.WriteLine("[AriaProcessService] Could not find aria2c executable.");
            return false;
        }

        try
        {
            var args = new List<string>
            {
                "--enable-rpc=true",
                "--rpc-allow-origin-all=true",
                "--rpc-listen-all=false",
                $"--rpc-listen-port={settings.RpcPort}",
                $"--rpc-secret={settings.RpcSecret}",
                $"--dir=\"{settings.DefaultDownloadDir}\"",
                $"--max-concurrent-downloads={settings.MaxConcurrentDownloads}",
                $"--max-connection-per-server={settings.MaxConnectionPerServer}",
                $"--split={settings.Split}",
                "--enable-dht=true",
                "--enable-peer-exchange=true",
                "--bt-enable-lpd=true",
                $"--dht-file-path=\"{_dhtFilePath}\"",
                $"--save-session=\"{_sessionFilePath}\"",
                "--save-session-interval=30",
                "--check-certificate=false"
            };

            if (File.Exists(_sessionFilePath) && new FileInfo(_sessionFilePath).Length > 0)
            {
                args.Add($"--input-file=\"{_sessionFilePath}\"");
            }

            if (settings.EnableBtTrackers && !string.IsNullOrWhiteSpace(settings.ExtraTrackers))
            {
                var formattedTrackers = settings.ExtraTrackers
                    .Replace("\r\n", ",")
                    .Replace("\n", ",")
                    .Trim(',');
                if (!string.IsNullOrWhiteSpace(formattedTrackers))
                {
                    args.Add($"--bt-tracker=\"{formattedTrackers}\"");
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Ensure LD_LIBRARY_PATH contains ~/.local/lib
            var localLib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "lib");
            var existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
            if (Directory.Exists(localLib) && !existingLd.Contains(localLib))
            {
                startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = $"{localLib}:{existingLd}";
            }

            _process = Process.Start(startInfo);
            if (_process == null || _process.HasExited)
            {
                Console.Error.WriteLine("[AriaProcessService] Failed to launch aria2c process.");
                return false;
            }

            // Save PID for tracking
            try
            {
                await File.WriteAllTextAsync(_pidFilePath, _process.Id.ToString(), cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaProcessService] Failed to write PID file: {ex.Message}");
            }

            _process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"[aria2c] {e.Data}"); };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"[aria2c-err] {e.Data}"); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Asynchronously wait for RPC port to become ready (up to 3 seconds)
            for (int i = 0; i < 15; i++)
            {
                if (await IsPortInUseAsync(settings.RpcPort, cancellationToken))
                {
                    return true;
                }
                await Task.Delay(200, cancellationToken);
            }

            return IsRunning;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AriaProcessService] Error starting aria2c: {ex.Message}");
            return false;
        }
    }

    public Task StopDaemonAsync(CancellationToken cancellationToken = default)
    {
        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(true);
                _process.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaProcessService] Error stopping aria2c: {ex.Message}");
            }
            finally
            {
                _process.Dispose();
                _process = null;
                try { if (File.Exists(_pidFilePath)) File.Delete(_pidFilePath); } catch { }
            }
        }
        return Task.CompletedTask;
    }
}
