using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;
using AriaUI.Services.Engine;

namespace AriaUI.Services.Gateway;

#region Linux Native Helpers

internal static class LinuxNative
{
    private const int SOL_SOCKET = 1;
    private const int SO_PEERCRED = 17;

    [StructLayout(LayoutKind.Sequential)]
    public struct LinuxUCred
    {
        public uint pid;
        public uint uid;
        public uint gid;
    }

    [DllImport("libc", SetLastError = true)]
    public static extern uint getuid();

    [DllImport("libc", SetLastError = true)]
    public static extern int chmod(string pathname, uint mode);

    [DllImport("libc", SetLastError = true)]
    public static extern int getsockopt(int sockfd, int level, int optname, out LinuxUCred optval, ref int optlen);

    public static bool VerifyPeerCredentials(Socket socket, out uint peerUid)
    {
        peerUid = uint.MaxValue;
        if (!OperatingSystem.IsLinux()) return true;

        try
        {
            var cred = new LinuxUCred();
            int len = Marshal.SizeOf<LinuxUCred>();
            int res = getsockopt((int)socket.Handle, SOL_SOCKET, SO_PEERCRED, out cred, ref len);
            if (res == 0)
            {
                peerUid = cred.uid;
                return cred.uid == getuid();
            }
        }
        catch
        {
            // fallback
        }
        return false;
    }
}

#endregion

#region Gateway Protocol DTOs

public sealed class GatewayMessage
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("extensionId")]
    public string? ExtensionId { get; set; }

    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }

    [JsonPropertyName("payload")]
    public GatewayPayload? Payload { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public sealed class GatewayPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("dir")]
    public string? Dir { get; set; }

    [JsonPropertyName("out")]
    public string? Out { get; set; }

    [JsonPropertyName("referer")]
    public string? Referer { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("headers")]
    public List<string>? Headers { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public sealed class GatewayResponse
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Success";

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }

    [JsonPropertyName("instanceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstanceId { get; set; }

    [JsonPropertyName("gid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Gid { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

#endregion

/// <summary>
/// Unix Domain Socket Gateway implementing §8.2–§8.4 and BOUNDARIES §5.
/// Provides strictly limited AddDownload and GetRequestResult commands with:
/// - 64 KiB max frame size
/// - Max 8 concurrent gateway connections
/// - Max 1 pending request per connection
/// - 10 req/s rate limit per extension
/// - SO_PEERCRED UID isolation
/// - 1,024 request de-duplication cache with 10 min TTL
/// </summary>
public sealed class AppGatewayService : IAsyncDisposable
{
    private sealed class CachedRequestRecord
    {
        public required string Key { get; init; }
        public required string PayloadHash { get; init; }
        public required string Status { get; set; }
        public string? Gid { get; set; }
        public string? Error { get; set; }
        public required DateTime Timestamp { get; init; }
    }

    private sealed class ExtensionRateLimiter
    {
        private readonly object _lock = new();
        private double _tokens = 10.0;
        private DateTime _lastRefill = DateTime.UtcNow;
        private const double Capacity = 10.0;
        private const double RefillRatePerSecond = 10.0;

        public bool TryAcquire()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastRefill).TotalSeconds;
                _tokens = Math.Min(Capacity, _tokens + elapsed * RefillRatePerSecond);
                _lastRefill = now;

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }
                return false;
            }
        }
    }

    private readonly IAriaTaskService _taskService;
    private readonly ISettingsService _settingsService;
    private readonly EngineRuntimeConfig _runtimeConfig;
    private readonly string _instanceId;
    private readonly string _socketPath;
    private readonly string _lockFilePath;

    private Socket? _listenerSocket;
    private FileStream? _lockFileStream;
    private Task? _acceptLoopTask;
    private readonly CancellationTokenSource _cts = new();
    private int _activeConnections;
    private int _isDisposed;

    private readonly ConcurrentDictionary<string, CachedRequestRecord> _records = new();
    private readonly ConcurrentDictionary<string, ExtensionRateLimiter> _rateLimiters = new();

    public string InstanceId => _instanceId;
    public string SocketPath => _socketPath;
    public bool IsListening => _listenerSocket != null;

    public AppGatewayService(
        IAriaTaskService taskService,
        ISettingsService settingsService,
        EngineRuntimeConfig? runtimeConfig = null,
        string? socketPathOverride = null,
        string? lockPathOverride = null)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _runtimeConfig = runtimeConfig ?? new EngineRuntimeConfig();
        _runtimeConfig.Validate();

        _instanceId = Guid.NewGuid().ToString("N");

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var baseDir = !string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir)
            ? Path.Combine(runtimeDir, "ariaui")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ariaui");

        Directory.CreateDirectory(baseDir);
        if (OperatingSystem.IsLinux())
        {
            LinuxNative.chmod(baseDir, 0x1C0); // 0700: rwx------
        }

        _socketPath = socketPathOverride ?? Path.Combine(baseDir, "gateway.sock");
        _lockFilePath = lockPathOverride ?? Path.Combine(baseDir, "ariaui.lock");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(AppGatewayService));

        // 1. Acquire single-instance lock
        try
        {
            _lockFileStream = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Another instance of AriaUI is already running (lock held at {_lockFilePath}).", ex);
        }

        // 2. Safely clean stale socket now that lock is held
        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }

        // 3. Create and bind Unix Domain Socket
        _listenerSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(_socketPath);
        _listenerSocket.Bind(endpoint);

        if (OperatingSystem.IsLinux())
        {
            LinuxNative.chmod(_socketPath, 0x180); // 0600: rw-------
        }

        _listenerSocket.Listen(16);

        // 4. Start background accept loop
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listenerSocket != null)
        {
            try
            {
                var clientSocket = await _listenerSocket.AcceptAsync(cancellationToken);

                // G05: UID Isolation Check
                if (!LinuxNative.VerifyPeerCredentials(clientSocket, out uint peerUid))
                {
                    Console.Error.WriteLine($"[AppGatewayService] Peer UID mismatch: connection rejected.");
                    clientSocket.Dispose();
                    continue;
                }

                // G04: Connection limit check (max 8)
                if (Interlocked.Increment(ref _activeConnections) > _runtimeConfig.MaxGatewayConnections)
                {
                    Interlocked.Decrement(ref _activeConnections);
                    Console.Error.WriteLine($"[AppGatewayService] Connection limit exceeded ({_runtimeConfig.MaxGatewayConnections}): connection rejected.");
                    await SendResponseAsync(clientSocket, new GatewayResponse
                    {
                        Status = "QueueFull",
                        Error = "Too many gateway connections."
                    }, cancellationToken);
                    clientSocket.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClientAsync(clientSocket, cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeConnections);
                        try { clientSocket.Dispose(); } catch { }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Console.Error.WriteLine($"[AppGatewayService] Accept loop error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken cancellationToken)
    {
        var networkStream = new NetworkStream(socket, ownsSocket: false);
        int pendingRequestsOnConnection = 0;
        var sendLock = new SemaphoreSlim(1, 1);
        var inFlightTasks = new List<Task>();

        while (!cancellationToken.IsCancellationRequested && socket.Connected)
        {
            // Read 4-byte length prefix (Native Messaging little-endian format)
            byte[] lenBytes = new byte[4];
            int readLen = await ReadExactBytesAsync(networkStream, lenBytes, 4, _runtimeConfig.GatewayInitialTimeout, cancellationToken);
            if (readLen < 4)
            {
                break; // EOF or disconnect
            }

            uint frameLength = BitConverter.ToUInt32(lenBytes, 0);

            // G03: 64 KiB boundary check before buffer allocation
            if (frameLength == 0 || frameLength > (uint)_runtimeConfig.GatewayMaxFrameSizeBytes)
            {
                Console.Error.WriteLine($"[AppGatewayService] Frame size out of bounds: {frameLength} bytes (max: {_runtimeConfig.GatewayMaxFrameSizeBytes}). Closing connection.");
                await sendLock.WaitAsync(cancellationToken);
                try
                {
                    await SendResponseAsync(socket, new GatewayResponse
                    {
                        Status = "FrameTooLarge",
                        Error = $"Frame exceeds 64 KiB boundary ({frameLength} bytes)."
                    }, cancellationToken);
                }
                finally
                {
                    sendLock.Release();
                }
                break;
            }

            // G04: Max 1 pending request per connection
            if (Interlocked.CompareExchange(ref pendingRequestsOnConnection, 1, 0) != 0)
            {
                Console.Error.WriteLine("[AppGatewayService] Multiple concurrent requests on single connection rejected.");
                await sendLock.WaitAsync(cancellationToken);
                try
                {
                    await SendResponseAsync(socket, new GatewayResponse
                    {
                        Status = "BadRequest",
                        Error = "Only 1 pending request per connection is allowed."
                    }, cancellationToken);
                }
                finally
                {
                    sendLock.Release();
                }
                break;
            }

            byte[] framePayload = new byte[frameLength];
            int readPayload = await ReadExactBytesAsync(networkStream, framePayload, (int)frameLength, _runtimeConfig.GatewayInitialTimeout, cancellationToken);
            if (readPayload < (int)frameLength)
            {
                Console.Error.WriteLine("[AppGatewayService] Premature EOF reading frame payload.");
                break;
            }

            var processingTask = Task.Run(async () =>
            {
                try
                {
                    var response = await ProcessRequestAsync(framePayload, cancellationToken);
                    await sendLock.WaitAsync(cancellationToken);
                    try
                    {
                        await SendResponseAsync(socket, response, cancellationToken);
                    }
                    finally
                    {
                        sendLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AppGatewayService] Request processing error: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref pendingRequestsOnConnection, 0);
                }
            }, cancellationToken);

            inFlightTasks.Add(processingTask);
        }

        try
        {
            await Task.WhenAll(inFlightTasks);
        }
        catch { }
    }

    private async Task<GatewayResponse> ProcessRequestAsync(byte[] payloadBytes, CancellationToken cancellationToken)
    {
        GatewayMessage? message;
        try
        {
            var json = Encoding.UTF8.GetString(payloadBytes);
            message = JsonSerializer.Deserialize<GatewayMessage>(json);
        }
        catch (Exception ex)
        {
            return new GatewayResponse { Status = "BadRequest", Error = $"JSON parse error: {ex.Message}" };
        }

        if (message == null)
        {
            return new GatewayResponse { Status = "BadRequest", Error = "Null message payload." };
        }

        // G01: Version and action validation
        if (message.Version != 1)
        {
            return new GatewayResponse { Status = "UnsupportedVersion", Error = $"Expected protocol version 1, got {message.Version}." };
        }

        if (message.ExtraFields != null && message.ExtraFields.Count > 0)
        {
            return new GatewayResponse { Status = "BadRequest", Error = "Unknown top-level fields are forbidden." };
        }

        switch (message.Action)
        {
            case "Handshake":
                return new GatewayResponse
                {
                    Status = "Success",
                    InstanceId = _instanceId,
                    Version = 1
                };

            case "AddDownload":
                return await HandleAddDownloadAsync(message, payloadBytes, cancellationToken);

            case "GetRequestResult":
                return HandleGetRequestResult(message);

            default:
                // G01: Reject any arbitrary RPC, shell, deletion, or global option operations
                return new GatewayResponse
                {
                    Status = "UnsupportedAction",
                    Error = $"Action '{message.Action}' is not permitted over gateway."
                };
        }
    }

    private async Task<GatewayResponse> HandleAddDownloadAsync(GatewayMessage message, byte[] rawPayload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 64)
            return new GatewayResponse { Status = "BadRequest", Error = "Valid requestId required (max 64 chars)." };

        if (string.IsNullOrWhiteSpace(message.ExtensionId) || message.ExtensionId.Length > 64)
            return new GatewayResponse { Status = "BadRequest", Error = "Valid extensionId required." };

        if (message.InstanceId != _instanceId)
            return new GatewayResponse { Status = "InstanceMismatch", RequestId = message.RequestId, Error = "App instance ID does not match." };

        var payload = message.Payload;
        if (payload == null)
            return new GatewayResponse { Status = "BadRequest", RequestId = message.RequestId, Error = "Missing payload." };

        if (payload.ExtraFields != null && payload.ExtraFields.Count > 0)
            return new GatewayResponse { Status = "BadRequest", RequestId = message.RequestId, Error = "Unknown payload fields are forbidden." };

        // G11: Only HTTP/HTTPS GET and magnet supported
        var url = payload.Url?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return new GatewayResponse { Status = "BadRequest", RequestId = message.RequestId, Error = "URL is required." };

        bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        bool isMagnet = url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);

        if (!isHttp && !isMagnet)
        {
            return new GatewayResponse
            {
                Status = "UnsupportedScheme",
                RequestId = message.RequestId,
                Error = "Only HTTP/HTTPS and magnet URLs are supported."
            };
        }

        // G03: Header injection, directory traversal & path escape checks
        if (!string.IsNullOrWhiteSpace(payload.Out))
        {
            if (payload.Out.Contains("..") || payload.Out.Contains('/') || payload.Out.Contains('\\'))
            {
                return new GatewayResponse
                {
                    Status = "InvalidPath",
                    RequestId = message.RequestId,
                    Error = "Output filename contains directory traversal characters."
                };
            }
        }

        string? validatedDir = null;
        if (!string.IsNullOrWhiteSpace(payload.Dir))
        {
            if (payload.Dir.Contains('\0') || payload.Dir.Contains('\r') || payload.Dir.Contains('\n') || payload.Dir.Contains(".."))
            {
                return new GatewayResponse
                {
                    Status = "InvalidPath",
                    RequestId = message.RequestId,
                    Error = "Directory contains invalid or traversal characters."
                };
            }

            var defaultDir = !string.IsNullOrWhiteSpace(_settingsService.Settings.DefaultDownloadDir)
                ? _settingsService.Settings.DefaultDownloadDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var safeRoot = Path.GetFullPath(defaultDir);
            var safePrefix = safeRoot.EndsWith(Path.DirectorySeparatorChar)
                ? safeRoot
                : safeRoot + Path.DirectorySeparatorChar;

            var fullDir = Path.IsPathRooted(payload.Dir)
                ? Path.GetFullPath(payload.Dir)
                : Path.GetFullPath(Path.Combine(safeRoot, payload.Dir));

            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(fullDir, safeRoot, pathComparison) && !fullDir.StartsWith(safePrefix, pathComparison))
            {
                return new GatewayResponse
                {
                    Status = "InvalidPath",
                    RequestId = message.RequestId,
                    Error = "Specified download directory is outside allowed download folder."
                };
            }
            validatedDir = fullDir;
        }

        if (payload.Headers != null)
        {
            if (payload.Headers.Count > 32)
            {
                return new GatewayResponse { Status = "BadRequest", RequestId = message.RequestId, Error = "Headers list exceeds 32 items." };
            }

            foreach (var h in payload.Headers)
            {
                if (h.Length > 8192)
                    return new GatewayResponse { Status = "BadRequest", RequestId = message.RequestId, Error = "Header line exceeds 8 KiB." };
                if (h.Contains('\r') || h.Contains('\n') || h.Contains('\0'))
                    return new GatewayResponse { Status = "HeaderInjection", RequestId = message.RequestId, Error = "Header contains newline or NUL character." };
            }
        }

        // G04: Rate limiting (10 req/s per extension)
        var limiter = _rateLimiters.GetOrAdd(message.ExtensionId, _ => new ExtensionRateLimiter());
        if (!limiter.TryAcquire())
        {
            return new GatewayResponse
            {
                Status = "RateLimited",
                RequestId = message.RequestId,
                Error = "Rate limit exceeded (max 10 requests per second)."
            };
        }

        // G08–G09: De-duplication and Record Capacity Management
        string cacheKey = $"{_instanceId}:{message.ExtensionId}:{message.RequestId}";
        string payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawPayload));

        CleanupExpiredRecords();

        if (_records.TryGetValue(cacheKey, out var existing))
        {
            if (existing.PayloadHash != payloadHash)
            {
                return new GatewayResponse
                {
                    Status = "Conflict",
                    RequestId = message.RequestId,
                    Error = "Request ID conflict with different payload."
                };
            }

            if (existing.Status == "Pending")
            {
                return new GatewayResponse
                {
                    Status = "Pending",
                    RequestId = message.RequestId
                };
            }

            return new GatewayResponse
            {
                Status = existing.Status,
                RequestId = message.RequestId,
                Gid = existing.Gid,
                Error = existing.Error
            };
        }

        if (_records.Count >= _runtimeConfig.GatewayRequestRecordCapacity)
        {
            return new GatewayResponse
            {
                Status = "QueueFull",
                RequestId = message.RequestId,
                Error = "Gateway request record cache is full."
            };
        }

        var newRecord = new CachedRequestRecord
        {
            Key = cacheKey,
            PayloadHash = payloadHash,
            Status = "Pending",
            Timestamp = DateTime.UtcNow
        };

        if (!_records.TryAdd(cacheKey, newRecord))
        {
            return new GatewayResponse { Status = "Pending", RequestId = message.RequestId };
        }

        // Execute task addition via task service
        try
        {
            var gid = await _taskService.AddUriAsync(
                url,
                saveDir: validatedDir,
                referer: payload.Referer,
                userAgent: payload.UserAgent,
                headers: payload.Headers,
                outFilename: payload.Out,
                cancellationToken: cancellationToken);

            newRecord.Status = "Success";
            newRecord.Gid = gid;

            return new GatewayResponse
            {
                Status = "Success",
                RequestId = message.RequestId,
                Gid = gid
            };
        }
        catch (Exception ex)
        {
            newRecord.Status = "Failed";
            newRecord.Error = ex.Message;

            return new GatewayResponse
            {
                Status = "Failed",
                RequestId = message.RequestId,
                Error = ex.Message
            };
        }
    }

    private GatewayResponse HandleGetRequestResult(GatewayMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.RequestId))
            return new GatewayResponse { Status = "BadRequest", Error = "Valid requestId required." };

        if (string.IsNullOrWhiteSpace(message.ExtensionId))
            return new GatewayResponse { Status = "BadRequest", Error = "Valid extensionId required." };

        if (message.InstanceId != _instanceId)
            return new GatewayResponse { Status = "InstanceMismatch", RequestId = message.RequestId, Error = "App instance ID does not match." };

        string cacheKey = $"{_instanceId}:{message.ExtensionId}:{message.RequestId}";
        if (_records.TryGetValue(cacheKey, out var record))
        {
            return new GatewayResponse
            {
                Status = record.Status,
                RequestId = message.RequestId,
                Gid = record.Gid,
                Error = record.Error
            };
        }

        return new GatewayResponse
        {
            Status = "UnknownOutcome",
            RequestId = message.RequestId,
            Error = "Request record not found or expired."
        };
    }

    private void CleanupExpiredRecords()
    {
        if (_records.Count < _runtimeConfig.GatewayRequestRecordCapacity) return;

        var now = DateTime.UtcNow;
        var expiredKeys = _records
            .Where(r => r.Value.Status != "Pending" && (now - r.Value.Timestamp > _runtimeConfig.GatewayRecordRetention))
            .Select(r => r.Key)
            .ToList();

        foreach (var k in expiredKeys)
        {
            _records.TryRemove(k, out _);
        }
    }

    private static async Task<int> ReadExactBytesAsync(Stream stream, byte[] buffer, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private static async Task SendResponseAsync(Socket socket, GatewayResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(json);
        byte[] lenBytes = BitConverter.GetBytes((uint)bytes.Length);

        await socket.SendAsync(lenBytes, SocketFlags.None, cancellationToken);
        await socket.SendAsync(bytes, SocketFlags.None, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        _cts.Cancel();

        if (_listenerSocket != null)
        {
            try { _listenerSocket.Close(); } catch { }
            try { _listenerSocket.Dispose(); } catch { }
            _listenerSocket = null;
        }

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }

        if (_acceptLoopTask != null)
        {
            try { await _acceptLoopTask; } catch { }
        }

        if (_lockFileStream != null)
        {
            try { await _lockFileStream.DisposeAsync(); } catch { }
            _lockFileStream = null;
        }

        _cts.Dispose();
    }
}
