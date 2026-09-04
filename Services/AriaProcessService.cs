using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;

namespace AriaUI.Services;

public interface IAriaProcessService : IDisposable
{
    bool IsRunning { get; }
    string? ExecutablePath { get; }
    Task<bool> StartDaemonAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task StopDaemonAsync(CancellationToken cancellationToken = default);
}

public class AriaProcessService : IAriaProcessService
{
    private static readonly HashSet<string> ManagedConfigOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "daemon",
        "enable-rpc",
        "rpc-allow-origin-all",
        "rpc-listen-all",
        "rpc-listen-port",
        "rpc-secret",
        "rpc-user",
        "rpc-passwd",
        "rpc-secure",
        "rpc-certificate",
        "rpc-private-key",
        "check-certificate",
        "dir",
        "max-concurrent-downloads",
        "max-connection-per-server",
        "split",
        "enable-dht",
        "enable-peer-exchange",
        "bt-enable-lpd",
        "dht-file-path",
        "save-session",
        "save-session-interval",
        "input-file"
    };

    private Process? _process;
    private readonly object _processStateLock = new();
    private readonly SemaphoreSlim _processOperationLock = new(1, 1);
    private readonly FileStream _instanceLockStream;
    private readonly string _sessionFilePath;
    private readonly string _dhtFilePath;
    private readonly string _pidFilePath;
    private readonly string _confFilePath;
    private int _isDisposed;

    public bool IsRunning
    {
        get
        {
            lock (_processStateLock)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }
    public string? ExecutablePath { get; private set; }

    public AriaProcessService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        var configDir = Path.Combine(appData, "AriaUI");
        Directory.CreateDirectory(configDir);
        _sessionFilePath = Path.Combine(configDir, "aria2.session");
        _dhtFilePath = Path.Combine(configDir, "dht.dat");
        _pidFilePath = Path.Combine(configDir, "aria2.pid");
        _confFilePath = Path.Combine(configDir, "aria2.conf");
        var instanceLockStream = new FileStream(
            Path.Combine(configDir, "ariaui.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        try
        {
            if (!File.Exists(_sessionFilePath))
            {
                File.Create(_sessionFilePath).Dispose();
            }

            _instanceLockStream = instanceLockStream;
        }
        catch
        {
            instanceLockStream.Dispose();
            throw;
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try
            {
                Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AriaProcessService] Process-exit cleanup failed: {ex}");
            }
        };
    }

    public string? FindAria2Executable(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        var candidateNames = OperatingSystem.IsWindows()
            ? new[] { "aria2c.exe", "aria2c" }
            : new[] { "aria2c" };

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in candidateNames)
        {
            var localBin = Path.Combine(userProfile, ".local", "bin", name);
            if (File.Exists(localBin))
            {
                return localBin;
            }
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            foreach (var name in candidateNames)
            {
                var full = Path.Combine(p, name);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private async Task CleanupOwnStaleProcessAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_pidFilePath)) return;

        bool cleanPid = false;
        try
        {
            var identity = await File.ReadAllTextAsync(_pidFilePath, cancellationToken);
            if (TryParseProcessIdentity(identity, out var pid, out var expectedStartTimeUtcTicks))
            {
                using var process = Process.GetProcessById(pid);
                var isOwnedProcess =
                    string.Equals(process.ProcessName, "aria2c", StringComparison.OrdinalIgnoreCase) &&
                    process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
                if (isOwnedProcess)
                {
                    process.Kill(true);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        await process.WaitForExitAsync(timeoutCts.Token);
                        cleanPid = true;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Timed out while stopping stale aria2c process {pid}.");
                    }
                }
                else
                {
                    // The PID was reused or the legacy process cannot be proven to belong to this app.
                    cleanPid = true;
                }
            }
            else
            {
                cleanPid = true;
            }
        }
        catch (ArgumentException)
        {
            // Process does not exist (already exited)
            cleanPid = true;
        }
        finally
        {
            if (cleanPid)
            {
                File.Delete(_pidFilePath);
            }
        }
    }

    private static bool TryParseProcessIdentity(
        string value,
        out int pid,
        out long startTimeUtcTicks)
    {
        pid = 0;
        startTimeUtcTicks = 0;
        var parts = value.Trim().Split('|');
        return parts.Length == 2 &&
               int.TryParse(parts[0], out pid) &&
               pid > 0 &&
               long.TryParse(parts[1], out startTimeUtcTicks) &&
               startTimeUtcTicks > 0;
    }

    private async Task<bool> IsPortInUseAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var connectHost = string.IsNullOrWhiteSpace(host)
                ? "127.0.0.1"
                : host.Trim().TrimStart('[').TrimEnd(']');
            await client.ConnectAsync(connectHost, port, linkedCts.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task<bool> IsAria2RpcRespondingAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectHost = string.IsNullOrWhiteSpace(settings.RpcHost) ? "127.0.0.1" : settings.RpcHost;
            connectHost = connectHost.Trim().TrimStart('[').TrimEnd(']');
            var scheme = settings.RpcUseTls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            var uri = new UriBuilder(scheme, connectHost, settings.RpcPort, "/jsonrpc").Uri;

            using var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = 64 * 1024
            };

            using var authenticatedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authenticatedCts.CancelAfter(TimeSpan.FromSeconds(1));
            var authenticatedProbe = await SendAria2ProbeAsync(
                httpClient,
                uri,
                settings.RpcSecret,
                "authenticated-probe",
                authenticatedCts.Token);
            if (!authenticatedProbe.IsSuccessStatusCode ||
                !authenticatedProbe.Body.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(version.GetString()))
            {
                return false;
            }

            using var rejectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rejectionCts.CancelAfter(TimeSpan.FromSeconds(3));
            var rejectedProbe = await SendAria2ProbeAsync(
                httpClient,
                uri,
                $"invalid-{Guid.NewGuid():N}",
                "auth-rejection-probe",
                rejectionCts.Token);
            return IsUnauthorizedResponse(rejectedProbe.Body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<(bool IsSuccessStatusCode, JsonElement Body)> SendAria2ProbeAsync(
        HttpClient httpClient,
        Uri uri,
        string secret,
        string requestId,
        CancellationToken cancellationToken)
    {
        var tokenJson = JsonSerializer.Serialize($"token:{secret}", AriaJsonContext.Default.String);
        var payload = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{requestId}\",\"method\":\"aria2.getVersion\",\"params\":[{tokenJson}]}}";
        using var content = new StringContent(payload, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var response = await httpClient.PostAsync(uri, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        return (response.IsSuccessStatusCode, document.RootElement.Clone());
    }

    private static bool IsUnauthorizedResponse(JsonElement response)
    {
        return response.TryGetProperty("error", out var error) &&
               error.ValueKind == JsonValueKind.Object &&
               error.TryGetProperty("message", out var message) &&
               message.ValueKind == JsonValueKind.String &&
               message.GetString()?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task WriteManagedConfigAsync(string rpcSecret, CancellationToken cancellationToken)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacyConfigPath = Path.Combine(userProfile, ".aria2", "aria2.conf");
        var standardConfigRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(standardConfigRoot) || !Path.IsPathRooted(standardConfigRoot))
        {
            standardConfigRoot = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Path.Combine(userProfile, ".config");
        }
        var standardConfigPath = Path.Combine(standardConfigRoot, "aria2", "aria2.conf");
        var userConfigPath = File.Exists(legacyConfigPath) ? legacyConfigPath : standardConfigPath;
        var configLines = new List<string>();

        if (File.Exists(userConfigPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(userConfigPath, cancellationToken))
            {
                if (!IsManagedConfigSetting(line))
                {
                    configLines.Add(line);
                }
            }
        }

        if (configLines.Count > 0 && !string.IsNullOrWhiteSpace(configLines[^1]))
        {
            configLines.Add(string.Empty);
        }
        configLines.Add($"rpc-secret={rpcSecret}");

        await using var configStream = new FileStream(
            _confFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_confFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await using var configWriter = new StreamWriter(configStream);
        foreach (var line in configLines)
        {
            await configWriter.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    private static bool IsManagedConfigSetting(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
        {
            return false;
        }

        var separatorIndex = trimmed.IndexOf('=');
        if (separatorIndex < 0)
        {
            return false;
        }

        var optionName = trimmed[..separatorIndex].Trim().TrimStart('-');
        return ManagedConfigOptions.Contains(optionName);
    }

    public async Task<bool> StartDaemonAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _processOperationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await StartDaemonCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _processOperationLock.Release();
        }
    }

    private async Task<bool> StartDaemonCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validationErrors = settings.Validate();
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", validationErrors), nameof(settings));
        }

        if (settings.AutoStartDaemon && settings.RpcUseTls)
        {
            throw new InvalidOperationException(
                "The managed local aria2c daemon has no RPC TLS certificate configured. Disable automatic daemon management when using TLS.");
        }

        Process? trackedProcess;
        lock (_processStateLock)
        {
            trackedProcess = _process;
            if (trackedProcess?.HasExited == true)
            {
                trackedProcess.Dispose();
                _process = null;
                trackedProcess = null;
            }
        }

        if (trackedProcess != null)
        {
            if (await IsAria2RpcRespondingAsync(settings, cancellationToken))
            {
                return true;
            }

            throw new InvalidOperationException("The tracked aria2c process is running but failed the authenticated RPC probe.");
        }

        // A prior process is only stopped when its PID and start time prove that this app launched it.
        await CleanupOwnStaleProcessAsync(cancellationToken);

        // If the port is still in use, verify with RPC authentication before treating it as external aria2.
        if (await IsPortInUseAsync(settings.RpcHost, settings.RpcPort, cancellationToken))
        {
            if (await IsAria2RpcRespondingAsync(settings, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"RPC port {settings.RpcHost}:{settings.RpcPort} belongs to an external authenticated aria2 instance. Disable automatic daemon management to connect to it.");
            }

            throw new InvalidOperationException(
                $"RPC port {settings.RpcHost}:{settings.RpcPort} is occupied by a service that failed the authenticated aria2 probe.");
        }

        ExecutablePath = FindAria2Executable(settings.Aria2ExecutablePath);
        if (ExecutablePath == null)
        {
            throw new FileNotFoundException("Could not find the aria2c executable.", settings.Aria2ExecutablePath);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("--enable-rpc=true");
            startInfo.ArgumentList.Add("--daemon=false");
            startInfo.ArgumentList.Add("--rpc-allow-origin-all=true");
            startInfo.ArgumentList.Add("--rpc-listen-all=false");
            startInfo.ArgumentList.Add($"--rpc-listen-port={settings.RpcPort}");
            startInfo.ArgumentList.Add("--rpc-secure=false");

            // Merge the user's standard aria2 config into a private managed config, then override
            // the secret without exposing it through the process command line.
            if (string.IsNullOrWhiteSpace(settings.RpcSecret) ||
                settings.RpcSecret.Contains('\r') ||
                settings.RpcSecret.Contains('\n') ||
                settings.RpcSecret != settings.RpcSecret.Trim())
            {
                throw new ArgumentException("RPC secret must be non-empty and cannot contain newlines or surrounding whitespace.", nameof(settings));
            }
            await WriteManagedConfigAsync(settings.RpcSecret, cancellationToken);
            startInfo.ArgumentList.Add($"--conf-path={_confFilePath}");

            if (!string.IsNullOrEmpty(settings.DefaultDownloadDir))
            {
                startInfo.ArgumentList.Add($"--dir={settings.DefaultDownloadDir}");
            }
            startInfo.ArgumentList.Add($"--max-concurrent-downloads={settings.MaxConcurrentDownloads}");
            startInfo.ArgumentList.Add($"--max-connection-per-server={settings.MaxConnectionPerServer}");
            startInfo.ArgumentList.Add($"--split={settings.Split}");
            startInfo.ArgumentList.Add("--enable-dht=true");
            startInfo.ArgumentList.Add("--enable-peer-exchange=true");
            startInfo.ArgumentList.Add("--bt-enable-lpd=true");
            startInfo.ArgumentList.Add($"--dht-file-path={_dhtFilePath}");
            startInfo.ArgumentList.Add($"--save-session={_sessionFilePath}");
            startInfo.ArgumentList.Add("--save-session-interval=30");

            startInfo.ArgumentList.Add($"--check-certificate={(!settings.AllowInvalidCert).ToString().ToLowerInvariant()}");

            if (File.Exists(_sessionFilePath) && new FileInfo(_sessionFilePath).Length > 0)
            {
                startInfo.ArgumentList.Add($"--input-file={_sessionFilePath}");
            }

            // Ensure LD_LIBRARY_PATH contains ~/.local/lib
            var localLib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "lib");
            var existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
            if (Directory.Exists(localLib) && !existingLd.Contains(localLib))
            {
                startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = $"{localLib}:{existingLd}";
            }

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to launch the aria2c process.");
            }
            lock (_processStateLock)
            {
                _process = process;
            }
            if (process.HasExited)
            {
                throw new InvalidOperationException($"aria2c exited immediately with code {process.ExitCode}.");
            }

            // Save PID for tracking
            var processIdentity = $"{process.Id}|{process.StartTime.ToUniversalTime().Ticks}";
            await File.WriteAllTextAsync(_pidFilePath, processIdentity, cancellationToken);

            process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"[aria2c] {e.Data}"); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"[aria2c-err] {e.Data}"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for an authenticated aria2 response, not merely an open TCP port.
            using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readinessCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"aria2c exited before RPC became ready (exit code {process.ExitCode}).");
                    }

                    if (await IsAria2RpcRespondingAsync(settings, readinessCts.Token))
                    {
                        if (process.HasExited)
                        {
                            throw new InvalidOperationException(
                                $"aria2c exited during the successful RPC readiness probe (exit code {process.ExitCode}).");
                        }
                        return true;
                    }
                    await Task.Delay(200, readinessCts.Token);
                }
            }
            catch (OperationCanceledException) when (
                readinessCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "aria2c did not pass the authenticated RPC readiness probe within 5 seconds.");
            }
        }
        catch (Exception startException)
        {
            try
            {
                await CleanupFailedStartAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException("aria2c startup failed and the started process could not be cleaned up.", startException, cleanupException);
            }
            throw;
        }
    }

    private async Task CleanupFailedStartAsync()
    {
        Process? process;
        lock (_processStateLock)
        {
            process = _process;
        }
        if (process == null)
        {
            File.Delete(_pidFilePath);
            File.Delete(_confFilePath);
            return;
        }

        if (!process.HasExited)
        {
            process.Kill(true);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Timed out while cleaning up aria2c after startup failure.");
            }
        }

        if (!process.HasExited)
        {
            throw new InvalidOperationException("aria2c remained alive after startup cleanup.");
        }

        lock (_processStateLock)
        {
            if (ReferenceEquals(_process, process))
            {
                process.Dispose();
                _process = null;
            }
        }
        File.Delete(_pidFilePath);
        File.Delete(_confFilePath);
    }

    public async Task StopDaemonAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _processOperationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await StopDaemonCoreAsync(cancellationToken);
        }
        finally
        {
            _processOperationLock.Release();
        }
    }

    private async Task StopDaemonCoreAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_processStateLock)
        {
            process = _process;
        }
        if (process == null)
        {
            await CleanupOwnStaleProcessAsync(cancellationToken);
            File.Delete(_confFilePath);
            return;
        }

        if (!process.HasExited)
        {
            process.Kill(true);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out while stopping aria2c.");
            }
        }

        if (!process.HasExited)
        {
            throw new InvalidOperationException("aria2c remained alive after the stop request.");
        }

        lock (_processStateLock)
        {
            if (ReferenceEquals(_process, process))
            {
                process.Dispose();
                _process = null;
            }
        }
        File.Delete(_pidFilePath);
        File.Delete(_confFilePath);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _processOperationLock.Wait();
        try
        {
            Process? process;
            lock (_processStateLock)
            {
                process = _process;
            }
            if (process == null)
            {
                return;
            }

            if (!process.HasExited)
            {
                process.Kill(true);
                if (!process.WaitForExit(2000))
                {
                    throw new TimeoutException("Timed out while disposing the managed aria2c process.");
                }
            }

            lock (_processStateLock)
            {
                if (ReferenceEquals(_process, process))
                {
                    process.Dispose();
                    _process = null;
                }
            }
            File.Delete(_pidFilePath);
            File.Delete(_confFilePath);
        }
        finally
        {
            _processOperationLock.Release();
            _instanceLockStream.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AriaProcessService));
        }
    }
}
