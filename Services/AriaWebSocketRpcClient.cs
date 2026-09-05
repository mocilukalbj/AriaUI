using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AriaUI.Models;

namespace AriaUI.Services;

public interface IAriaRpcClient : IAsyncDisposable
{
    bool IsConnected { get; }
    event EventHandler? ConnectionStateChanged;
    event EventHandler<string>? DownloadStarted;
    event EventHandler<string>? DownloadCompleted;
    event EventHandler<string>? DownloadError;
    event EventHandler<string>? DownloadPaused;
    event EventHandler<string>? DownloadStopped;

    Task ConnectAsync(string host, int port, string secret, bool useTls = false, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<string> AddUriAsync(IEnumerable<string> uris, Dictionary<string, object>? options = null, CancellationToken cancellationToken = default);
    Task<string> AddTorrentAsync(byte[] torrentBytes, Dictionary<string, object>? options = null, CancellationToken cancellationToken = default);
    Task<List<AriaTaskInfo>> TellActiveAsync(CancellationToken cancellationToken = default);
    Task<List<AriaTaskInfo>> TellWaitingAsync(int offset = 0, int num = 100, CancellationToken cancellationToken = default);
    Task<List<AriaTaskInfo>> TellStoppedAsync(int offset = 0, int num = 100, CancellationToken cancellationToken = default);
    Task<AriaTaskInfo> TellStatusAsync(string gid, CancellationToken cancellationToken = default);
    Task<string> PauseAsync(string gid, CancellationToken cancellationToken = default);
    Task<string> PauseAllAsync(CancellationToken cancellationToken = default);
    Task<string> UnpauseAsync(string gid, CancellationToken cancellationToken = default);
    Task<string> UnpauseAllAsync(CancellationToken cancellationToken = default);
    Task<string> RemoveAsync(string gid, CancellationToken cancellationToken = default);
    Task<string> ForceRemoveAsync(string gid, CancellationToken cancellationToken = default);
    Task<string> RemoveDownloadResultAsync(string gid, CancellationToken cancellationToken = default);
    Task<string> PurgeDownloadResultAsync(CancellationToken cancellationToken = default);
    Task<AriaGlobalStat> GetGlobalStatAsync(CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetGlobalOptionAsync(CancellationToken cancellationToken = default);
    Task<string> ChangeGlobalOptionAsync(Dictionary<string, object> options, CancellationToken cancellationToken = default);
    Task<string> ShutdownAsync(CancellationToken cancellationToken = default);
}

public class AriaWebSocketRpcClient : IAriaRpcClient
{
    internal sealed class ConnectionContext(ClientWebSocket socket, CancellationTokenSource cts, long generation, string secret)
    {
        public ClientWebSocket Socket { get; } = socket;
        public CancellationTokenSource Cts { get; } = cts;
        public CancellationToken Token { get; } = cts.Token;
        public long Generation { get; } = generation;
        public string Secret { get; } = secret;
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> PendingRequests { get; } = new();
        public Task? ReceiveTask { get; set; }
        public int IsRetired;
    }

    private ConnectionContext? _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private long _connectionGeneration;
    private volatile bool _isConnected;

    public bool IsConnected
    {
        get => _isConnected;
    }

    public event EventHandler? ConnectionStateChanged;
    public event EventHandler<string>? DownloadStarted;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<string>? DownloadError;
    public event EventHandler<string>? DownloadPaused;
    public event EventHandler<string>? DownloadStopped;

    public async Task ConnectAsync(string host, int port, string secret, bool useTls = false, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();

            var formattedHost = host.Contains(':') && !host.StartsWith("[") && !host.EndsWith("]") ? $"[{host}]" : host;
            var scheme = useTls ? "wss" : "ws";
            var uri = new Uri($"{scheme}://{formattedHost}:{port}/jsonrpc");
            var socket = new ClientWebSocket();
            var cts = new CancellationTokenSource();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await socket.ConnectAsync(uri, linkedCts.Token);
            }
            catch
            {
                socket.Dispose();
                cts.Dispose();
                throw;
            }

            var connection = new ConnectionContext(
                socket,
                cts,
                Interlocked.Increment(ref _connectionGeneration),
                secret);
            lock (_stateLock)
            {
                _connection = connection;
            }
            connection.ReceiveTask = ReceiveLoopAsync(connection);

            try
            {
                var invalidSecret = CreateInvalidSecret(secret);
                var invalidSecretRejected = false;
                try
                {
                    _ = await InvokeOnConnectionWithSecretAsync<AriaVersionInfo>(
                        connection,
                        "aria2.getVersion",
                        linkedCts.Token,
                        invalidSecret);
                }
                catch (AriaRpcException ex) when (ex.IsUnauthorized)
                {
                    invalidSecretRejected = true;
                }
                catch (AriaRpcException ex)
                {
                    throw new InvalidDataException(
                        "aria2 RPC authentication probe returned an unexpected error.",
                        ex);
                }

                if (!invalidSecretRejected)
                {
                    throw new InvalidDataException(
                        "The RPC endpoint accepted an invalid token; rpc-secret authentication is not enforced.");
                }

                var version = await InvokeOnConnectionAsync<AriaVersionInfo>(
                    connection,
                    "aria2.getVersion",
                    linkedCts.Token);
                if (string.IsNullOrWhiteSpace(version.Version))
                {
                    throw new InvalidDataException("aria2 RPC authentication returned an invalid version.");
                }
                if (!TryPublishConnected(connection))
                {
                    throw new IOException("WebSocket closed during aria2 RPC authentication.");
                }
            }
            catch
            {
                await DisconnectCoreAsync();
                throw;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static string CreateInvalidSecret(string actualSecret)
    {
        string invalidSecret;
        do
        {
            invalidSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
        while (string.Equals(invalidSecret, actualSecret, StringComparison.Ordinal));

        return invalidSecret;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        ConnectionContext? connection;
        lock (_stateLock)
        {
            connection = _connection;
        }

        if (connection == null)
        {
            return;
        }

        var receiveTask = connection.ReceiveTask;
        RetireConnection(connection, new OperationCanceledException("WebSocket connection closed."));
        if (receiveTask != null)
        {
            await receiveTask;
        }
    }

    private async Task ReceiveLoopAsync(ConnectionContext connection)
    {
        var socket = connection.Socket;
        var ct = connection.Token;
        var buffer = new byte[64 * 1024];
        Exception? failure = null;

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > 8 * 1024 * 1024)
                    {
                        throw new InvalidOperationException("WebSocket message exceeded maximum allowed size (8MB).");
                    }
                } while (!result.EndOfMessage);

                var messageJson = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                try
                {
                    ProcessIncomingMessage(connection, messageJson);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AriaWebSocketRpcClient] Error processing incoming message: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            RetireConnection(connection, failure ?? new IOException("WebSocket disconnected."));
        }
    }

    private bool TryPublishConnected(ConnectionContext connection)
    {
        bool stateChanged;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_connection, connection) ||
                Volatile.Read(ref connection.IsRetired) != 0 ||
                connection.Socket.State != WebSocketState.Open)
            {
                return false;
            }

            stateChanged = !_isConnected;
            _isConnected = true;
        }

        if (stateChanged)
        {
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        return IsCurrentConnection(connection);
    }

    private void RetireConnection(ConnectionContext connection, Exception exception)
    {
        bool stateChanged = false;
        lock (_stateLock)
        {
            if (connection.IsRetired != 0)
            {
                return;
            }

            Volatile.Write(ref connection.IsRetired, 1);
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
                stateChanged = _isConnected;
                _isConnected = false;
            }
        }

        FailPending(connection, exception);
        try
        {
            connection.Cts.Cancel();
            connection.Socket.Abort();
        }
        finally
        {
            connection.Socket.Dispose();
            connection.Cts.Dispose();
        }

        if (stateChanged)
        {
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsCurrentConnection(ConnectionContext connection)
    {
        lock (_stateLock)
        {
            return ReferenceEquals(_connection, connection) &&
                   Volatile.Read(ref connection.IsRetired) == 0;
        }
    }

    private ConnectionContext GetConnectedContext()
    {
        lock (_stateLock)
        {
            if (!_isConnected ||
                _connection == null ||
                Volatile.Read(ref _connection.IsRetired) != 0 ||
                _connection.Socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not connected to aria2c.");
            }

            return _connection;
        }
    }

    private static void FailPending(ConnectionContext connection, Exception exception)
    {
        foreach (var request in connection.PendingRequests)
        {
            if (connection.PendingRequests.TryRemove(request.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    internal void ProcessIncomingMessage(ConnectionContext connection, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[AriaWebSocketRpcClient] Failed to parse JSON message: {ex.Message}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var jsonRpc) ||
                jsonRpc.ValueKind != JsonValueKind.String ||
                !string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[AriaWebSocketRpcClient] Received non-JSON-RPC-2.0 message: {json}");
                return;
            }

            if (root.TryGetProperty("id", out var idProp))
            {
                string? id = idProp.ValueKind switch
                {
                    JsonValueKind.String => idProp.GetString(),
                    JsonValueKind.Number => idProp.GetInt64().ToString(),
                    _ => null
                };

                if (string.IsNullOrEmpty(id))
                {
                    Console.Error.WriteLine($"[AriaWebSocketRpcClient] RPC message contains null or invalid request id: {json}");
                    return;
                }

                if (connection.PendingRequests.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind != JsonValueKind.Null && errorProp.ValueKind != JsonValueKind.Undefined)
                    {
                        if (errorProp.ValueKind != JsonValueKind.Object ||
                            !errorProp.TryGetProperty("code", out var codeProp) ||
                            !codeProp.TryGetInt32(out var code) ||
                            !errorProp.TryGetProperty("message", out var msgProp) ||
                            msgProp.ValueKind != JsonValueKind.String)
                        {
                            tcs.TrySetException(new InvalidDataException("RPC response contains an invalid error object."));
                        }
                        else
                        {
                            tcs.TrySetException(new AriaRpcException(code, msgProp.GetString() ?? string.Empty));
                        }
                    }
                    else if (root.TryGetProperty("result", out var resultProp))
                    {
                        tcs.TrySetResult(resultProp.Clone());
                    }
                    else
                    {
                        tcs.TrySetException(new InvalidDataException("RPC response contains neither result nor error."));
                    }
                }
                else
                {
                    Console.Error.WriteLine($"[AriaWebSocketRpcClient] Received response for unknown or expired request id: {id}");
                }
            }
            else if (root.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                var gid = string.Empty;
                if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Array && paramsProp.GetArrayLength() > 0)
                {
                    var p0 = paramsProp[0];
                    if (p0.ValueKind == JsonValueKind.Object && p0.TryGetProperty("gid", out var gidProp))
                    {
                        gid = gidProp.GetString() ?? string.Empty;
                    }
                }

                if (!IsValidGid(gid))
                {
                    Console.Error.WriteLine($"[AriaWebSocketRpcClient] Notification {method} contained missing or invalid GID: '{gid}'");
                    return;
                }

                switch (method)
                {
                    case "aria2.onDownloadStart":
                        DownloadStarted?.Invoke(this, gid);
                        break;
                    case "aria2.onDownloadComplete":
                        DownloadCompleted?.Invoke(this, gid);
                        break;
                    case "aria2.onDownloadError":
                        DownloadError?.Invoke(this, gid);
                        break;
                    case "aria2.onDownloadPause":
                        DownloadPaused?.Invoke(this, gid);
                        break;
                    case "aria2.onDownloadStop":
                        DownloadStopped?.Invoke(this, gid);
                        break;
                    default:
                        Console.Error.WriteLine($"[AriaWebSocketRpcClient] Received unknown notification method: {method}");
                        break;
                }
            }
            else
            {
                Console.Error.WriteLine($"[AriaWebSocketRpcClient] Message without id or method: {json}");
            }
        }
    }

    private async Task<T> InvokeAsync<T>(string method, CancellationToken cancellationToken, params object[] parameters)
        => await InvokeOnConnectionAsync<T>(GetConnectedContext(), method, cancellationToken, parameters);

    private async Task<T> InvokeOnConnectionAsync<T>(
        ConnectionContext connection,
        string method,
        CancellationToken cancellationToken,
        params object[] parameters)
        => await InvokeOnConnectionWithSecretAsync<T>(
            connection,
            method,
            cancellationToken,
            connection.Secret,
            parameters);

    private async Task<T> InvokeOnConnectionWithSecretAsync<T>(
        ConnectionContext connection,
        string method,
        CancellationToken cancellationToken,
        string authenticationSecret,
        params object[] parameters)
    {
        var socket = connection.Socket;
        if (!IsCurrentConnection(connection) || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected to aria2c.");
        }

        var reqId = Guid.NewGuid().ToString("N");
        var paramList = new List<object>();

        if (!string.IsNullOrEmpty(authenticationSecret))
        {
            paramList.Add($"token:{authenticationSecret}");
        }
        paramList.AddRange(parameters);

        var rpcReq = new RpcRequest
        {
            Id = reqId,
            Method = method,
            Params = paramList.ToArray()
        };

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            if (!ReferenceEquals(_connection, connection) ||
                connection.IsRetired != 0 ||
                socket.State != WebSocketState.Open)
            {
                throw new IOException(
                    $"WebSocket connection generation {connection.Generation} was retired before the RPC request was registered.");
            }

            if (!connection.PendingRequests.TryAdd(reqId, tcs))
            {
                throw new InvalidOperationException($"Duplicate RPC request id generated: {reqId}.");
            }
        }

        try
        {
            var json = JsonSerializer.Serialize(rpcReq, AriaJsonContext.Default.RpcRequest);
            var bytes = Encoding.UTF8.GetBytes(json);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token,
                connection.Token);
            await _sendLock.WaitAsync(linkedCts.Token);
            try
            {
                Task sendTask;
                lock (_stateLock)
                {
                    if (!ReferenceEquals(_connection, connection) ||
                        connection.IsRetired != 0 ||
                        socket.State != WebSocketState.Open)
                    {
                        throw new IOException(
                            $"WebSocket connection generation {connection.Generation} was retired before the RPC request was sent.");
                    }

                    sendTask = socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        linkedCts.Token);
                }
                await sendTask;
            }
            finally
            {
                _sendLock.Release();
            }

            using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
            {
                var resultElement = await tcs.Task;
                if (resultElement.ValueKind == JsonValueKind.Undefined || resultElement.ValueKind == JsonValueKind.Null)
                {
                    throw new InvalidDataException($"RPC method {method} returned no result.");
                }

                var typeInfo = AriaJsonContext.Default.GetTypeInfo(typeof(T));
                if (typeInfo == null)
                {
                    throw new InvalidOperationException($"No JSON metadata registered for {typeof(T).FullName}; add it to AriaJsonContext.");
                }
                return (T?)JsonSerializer.Deserialize(resultElement.GetRawText(), typeInfo)
                    ?? throw new InvalidDataException($"RPC method {method} returned an invalid {typeof(T).Name} result.");
            }
        }
        finally
        {
            connection.PendingRequests.TryRemove(reqId, out _);
        }
    }

    public async Task<string> AddUriAsync(IEnumerable<string> uris, Dictionary<string, object>? options = null, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync<string>("aria2.addUri", cancellationToken, uris, options ?? new Dictionary<string, object>());
        return RequireGid("aria2.addUri", result);
    }

    public async Task<string> AddTorrentAsync(byte[] torrentBytes, Dictionary<string, object>? options = null, CancellationToken cancellationToken = default)
    {
        var base64 = Convert.ToBase64String(torrentBytes);
        var result = await InvokeAsync<string>("aria2.addTorrent", cancellationToken, base64, Array.Empty<string>(), options ?? new Dictionary<string, object>());
        return RequireGid("aria2.addTorrent", result);
    }

    public async Task<List<AriaTaskInfo>> TellActiveAsync(CancellationToken cancellationToken = default)
    {
        return await InvokeAsync<List<AriaTaskInfo>>("aria2.tellActive", cancellationToken);
    }

    public async Task<List<AriaTaskInfo>> TellWaitingAsync(int offset = 0, int num = 100, CancellationToken cancellationToken = default)
    {
        return await InvokeAsync<List<AriaTaskInfo>>("aria2.tellWaiting", cancellationToken, offset, num);
    }

    public async Task<List<AriaTaskInfo>> TellStoppedAsync(int offset = 0, int num = 100, CancellationToken cancellationToken = default)
    {
        return await InvokeAsync<List<AriaTaskInfo>>("aria2.tellStopped", cancellationToken, offset, num);
    }

    public async Task<AriaTaskInfo> TellStatusAsync(string gid, CancellationToken cancellationToken = default)
    {
        var expectedGid = RequireGid("aria2.tellStatus request", gid);
        var result = await InvokeAsync<AriaTaskInfo>("aria2.tellStatus", cancellationToken, expectedGid);
        _ = RequireExpectedGid("aria2.tellStatus", result.Gid, expectedGid);
        return result;
    }

    public async Task<string> PauseAsync(string gid, CancellationToken cancellationToken = default)
    {
        var expectedGid = RequireGid("aria2.pause request", gid);
        var res = await InvokeAsync<string>("aria2.pause", cancellationToken, expectedGid);
        return RequireExpectedGid("aria2.pause", res, expectedGid);
    }

    public async Task<string> PauseAllAsync(CancellationToken cancellationToken = default)
    {
        var res = await InvokeAsync<string>("aria2.pauseAll", cancellationToken);
        return RequireOk("aria2.pauseAll", res);
    }

    public async Task<string> UnpauseAsync(string gid, CancellationToken cancellationToken = default)
    {
        var expectedGid = RequireGid("aria2.unpause request", gid);
        var res = await InvokeAsync<string>("aria2.unpause", cancellationToken, expectedGid);
        return RequireExpectedGid("aria2.unpause", res, expectedGid);
    }

    public async Task<string> UnpauseAllAsync(CancellationToken cancellationToken = default)
    {
        var res = await InvokeAsync<string>("aria2.unpauseAll", cancellationToken);
        return RequireOk("aria2.unpauseAll", res);
    }

    public async Task<string> RemoveAsync(string gid, CancellationToken cancellationToken = default)
    {
        var expectedGid = RequireGid("aria2.remove request", gid);
        var res = await InvokeAsync<string>("aria2.remove", cancellationToken, expectedGid);
        return RequireExpectedGid("aria2.remove", res, expectedGid);
    }

    public async Task<string> ForceRemoveAsync(string gid, CancellationToken cancellationToken = default)
    {
        var expectedGid = RequireGid("aria2.forceRemove request", gid);
        var res = await InvokeAsync<string>("aria2.forceRemove", cancellationToken, expectedGid);
        return RequireExpectedGid("aria2.forceRemove", res, expectedGid);
    }

    public async Task<string> RemoveDownloadResultAsync(string gid, CancellationToken cancellationToken = default)
    {
        var expectedGid = RequireGid("aria2.removeDownloadResult request", gid);
        var res = await InvokeAsync<string>("aria2.removeDownloadResult", cancellationToken, expectedGid);
        return RequireOk("aria2.removeDownloadResult", res);
    }

    public async Task<string> PurgeDownloadResultAsync(CancellationToken cancellationToken = default)
    {
        var res = await InvokeAsync<string>("aria2.purgeDownloadResult", cancellationToken);
        return RequireOk("aria2.purgeDownloadResult", res);
    }

    public async Task<AriaGlobalStat> GetGlobalStatAsync(CancellationToken cancellationToken = default)
    {
        return await InvokeAsync<AriaGlobalStat>("aria2.getGlobalStat", cancellationToken);
    }

    public async Task<Dictionary<string, string>> GetGlobalOptionAsync(CancellationToken cancellationToken = default)
    {
        return await InvokeAsync<Dictionary<string, string>>("aria2.getGlobalOption", cancellationToken);
    }

    public async Task<string> ChangeGlobalOptionAsync(Dictionary<string, object> options, CancellationToken cancellationToken = default)
    {
        var res = await InvokeAsync<string>("aria2.changeGlobalOption", cancellationToken, options);
        return RequireOk("aria2.changeGlobalOption", res);
    }

    public async Task<string> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var res = await InvokeAsync<string>("aria2.shutdown", cancellationToken);
        return RequireOk("aria2.shutdown", res);
    }

    private static bool IsValidGid(string? gid)
    {
        return !string.IsNullOrEmpty(gid) &&
               gid.Length == 16 &&
               !gid.All(static ch => ch == '0') &&
               gid.All(static ch => char.IsAsciiHexDigit(ch));
    }

    private static string RequireGid(string method, string result)
    {
        if (!IsValidGid(result))
        {
            throw new InvalidDataException($"RPC method {method} returned an invalid GID: {result}.");
        }

        return result;
    }

    private static string RequireOk(string method, string result)
    {
        if (!string.Equals(result, "OK", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"RPC method {method} returned an unexpected result: {result}.");
        }

        return result;
    }

    private static string RequireExpectedGid(string method, string result, string expectedGid)
    {
        var validatedGid = RequireGid(method, result);
        if (!string.Equals(validatedGid, expectedGid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"RPC method {method} returned GID {validatedGid}, expected {expectedGid}.");
        }

        return validatedGid;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
