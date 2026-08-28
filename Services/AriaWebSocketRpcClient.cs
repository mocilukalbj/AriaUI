using System.Collections.Concurrent;
using System.Net.WebSockets;
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

    Task ConnectAsync(string host, int port, string secret, CancellationToken cancellationToken = default);
    Task DisconnectAsync();

    Task<string> AddUriAsync(IEnumerable<string> uris, Dictionary<string, object>? options = null);
    Task<string> AddTorrentAsync(byte[] torrentBytes, Dictionary<string, object>? options = null);
    Task<List<AriaTaskInfo>> TellActiveAsync();
    Task<List<AriaTaskInfo>> TellWaitingAsync(int offset = 0, int num = 100);
    Task<List<AriaTaskInfo>> TellStoppedAsync(int offset = 0, int num = 100);
    Task<AriaTaskInfo?> TellStatusAsync(string gid);
    Task<string> PauseAsync(string gid);
    Task<string> PauseAllAsync();
    Task<string> UnpauseAsync(string gid);
    Task<string> UnpauseAllAsync();
    Task<string> RemoveAsync(string gid);
    Task<string> ForceRemoveAsync(string gid);
    Task<string> RemoveDownloadResultAsync(string gid);
    Task<string> PurgeDownloadResultAsync();
    Task<AriaGlobalStat?> GetGlobalStatAsync();
    Task<Dictionary<string, string>?> GetGlobalOptionAsync();
    Task<string> ChangeGlobalOptionAsync(Dictionary<string, object> options);
}

public class AriaWebSocketRpcClient : IAriaRpcClient
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private string _secret = string.Empty;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private bool _isConnected;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? ConnectionStateChanged;
    public event EventHandler<string>? DownloadStarted;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<string>? DownloadError;
    public event EventHandler<string>? DownloadPaused;
    public event EventHandler<string>? DownloadStopped;

    public async Task ConnectAsync(string host, int port, string secret, CancellationToken cancellationToken = default)
    {
        _secret = secret;
        await DisconnectAsync();

        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();

        var scheme = (port == 443) ? "wss" : "ws";
        var formattedHost = host.Contains(':') && !host.StartsWith("[") && !host.EndsWith("]") ? $"[{host}]" : host;
        var uri = new Uri($"{scheme}://{formattedHost}:{port}/jsonrpc");

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await _webSocket.ConnectAsync(uri, linkedCts.Token);
            IsConnected = true;

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }
        catch
        {
            IsConnected = false;
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch { }
            }
            _webSocket.Dispose();
            _webSocket = null;
        }

        IsConnected = false;

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
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
                ProcessIncomingMessage(messageJson);
            }
        }
        catch
        {
            // Socket disconnected
        }
        finally
        {
            IsConnected = false;
        }
    }

    private void ProcessIncomingMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id) && _pendingRequests.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.Object)
                    {
                        string msg = "RPC error";
                        if (errorProp.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
                        {
                            msg = msgProp.GetString() ?? "RPC error";
                        }
                        else
                        {
                            msg = errorProp.GetRawText();
                        }
                        tcs.TrySetException(new Exception(msg));
                    }
                    else if (root.TryGetProperty("result", out var resultProp))
                    {
                        tcs.TrySetResult(resultProp.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(default);
                    }
                }
            }
            else if (root.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                var gid = string.Empty;
                if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Array && paramsProp.GetArrayLength() > 0)
                {
                    var p0 = paramsProp[0];
                    if (p0.TryGetProperty("gid", out var gidProp))
                    {
                        gid = gidProp.GetString() ?? string.Empty;
                    }
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
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error processing RPC message: {ex.Message}");
        }
    }

    private async Task<T?> InvokeAsync<T>(string method, params object[] parameters)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected to aria2c.");
        }

        var reqId = Guid.NewGuid().ToString("N");
        var paramList = new List<object>();

        if (!string.IsNullOrEmpty(_secret))
        {
            paramList.Add($"token:{_secret}");
        }
        paramList.AddRange(parameters);

        var rpcReq = new RpcRequest
        {
            Id = reqId,
            Method = method,
            Params = paramList.ToArray()
        };

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[reqId] = tcs;

        try
        {
            var json = JsonSerializer.Serialize(rpcReq, AriaJsonContext.Default.RpcRequest);
            var bytes = Encoding.UTF8.GetBytes(json);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeoutCts.Token);

            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                var resultElement = await tcs.Task;
                if (resultElement.ValueKind == JsonValueKind.Undefined || resultElement.ValueKind == JsonValueKind.Null)
                {
                    return default;
                }

                var typeInfo = AriaJsonContext.Default.GetTypeInfo(typeof(T));
                if (typeInfo != null)
                {
                    return (T?)JsonSerializer.Deserialize(resultElement.GetRawText(), typeInfo);
                }

                return JsonSerializer.Deserialize<T>(resultElement.GetRawText());
            }
        }
        finally
        {
            _pendingRequests.TryRemove(reqId, out _);
        }
    }

    public async Task<string> AddUriAsync(IEnumerable<string> uris, Dictionary<string, object>? options = null)
    {
        var result = await InvokeAsync<string>("aria2.addUri", uris, options ?? new Dictionary<string, object>());
        return result ?? string.Empty;
    }

    public async Task<string> AddTorrentAsync(byte[] torrentBytes, Dictionary<string, object>? options = null)
    {
        var base64 = Convert.ToBase64String(torrentBytes);
        var result = await InvokeAsync<string>("aria2.addTorrent", base64, new string[0], options ?? new Dictionary<string, object>());
        return result ?? string.Empty;
    }

    public async Task<List<AriaTaskInfo>> TellActiveAsync()
    {
        var result = await InvokeAsync<List<AriaTaskInfo>>("aria2.tellActive");
        return result ?? new List<AriaTaskInfo>();
    }

    public async Task<List<AriaTaskInfo>> TellWaitingAsync(int offset = 0, int num = 100)
    {
        var result = await InvokeAsync<List<AriaTaskInfo>>("aria2.tellWaiting", offset, num);
        return result ?? new List<AriaTaskInfo>();
    }

    public async Task<List<AriaTaskInfo>> TellStoppedAsync(int offset = 0, int num = 100)
    {
        var result = await InvokeAsync<List<AriaTaskInfo>>("aria2.tellStopped", offset, num);
        return result ?? new List<AriaTaskInfo>();
    }

    public async Task<AriaTaskInfo?> TellStatusAsync(string gid)
    {
        return await InvokeAsync<AriaTaskInfo>("aria2.tellStatus", gid);
    }

    public async Task<string> PauseAsync(string gid)
    {
        var res = await InvokeAsync<string>("aria2.pause", gid);
        return res ?? string.Empty;
    }

    public async Task<string> PauseAllAsync()
    {
        var res = await InvokeAsync<string>("aria2.pauseAll");
        return res ?? string.Empty;
    }

    public async Task<string> UnpauseAsync(string gid)
    {
        var res = await InvokeAsync<string>("aria2.unpause", gid);
        return res ?? string.Empty;
    }

    public async Task<string> UnpauseAllAsync()
    {
        var res = await InvokeAsync<string>("aria2.unpauseAll");
        return res ?? string.Empty;
    }

    public async Task<string> RemoveAsync(string gid)
    {
        var res = await InvokeAsync<string>("aria2.remove", gid);
        return res ?? string.Empty;
    }

    public async Task<string> ForceRemoveAsync(string gid)
    {
        var res = await InvokeAsync<string>("aria2.forceRemove", gid);
        return res ?? string.Empty;
    }

    public async Task<string> RemoveDownloadResultAsync(string gid)
    {
        var res = await InvokeAsync<string>("aria2.removeDownloadResult", gid);
        return res ?? string.Empty;
    }

    public async Task<string> PurgeDownloadResultAsync()
    {
        var res = await InvokeAsync<string>("aria2.purgeDownloadResult");
        return res ?? string.Empty;
    }

    public async Task<AriaGlobalStat?> GetGlobalStatAsync()
    {
        return await InvokeAsync<AriaGlobalStat>("aria2.getGlobalStat");
    }

    public async Task<Dictionary<string, string>?> GetGlobalOptionAsync()
    {
        return await InvokeAsync<Dictionary<string, string>>("aria2.getGlobalOption");
    }

    public async Task<string> ChangeGlobalOptionAsync(Dictionary<string, object> options)
    {
        var res = await InvokeAsync<string>("aria2.changeGlobalOption", options);
        return res ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
