using System.Text.Json;
using System.Text.Json.Serialization;

namespace AriaUI.Models;

public class RpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object[]? Params { get; set; }
}

public class RpcResponse<T>
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("error")]
    public RpcError? Error { get; set; }
}

public class RpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement[]? Params { get; set; }
}

public class RpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class AriaRpcException(int code, string rpcMessage)
    : Exception($"aria2 RPC error {code}: {rpcMessage}")
{
    public int Code { get; } = code;
    public string RpcMessage { get; } = rpcMessage;

    public bool IsUnauthorized =>
        Code == 1 &&
        string.Equals(RpcMessage.Trim().TrimEnd('.'), "Unauthorized", StringComparison.OrdinalIgnoreCase);
}

public class AriaVersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcError))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<AriaTaskInfo>))]
[JsonSerializable(typeof(AriaTaskInfo))]
[JsonSerializable(typeof(AriaGlobalStat))]
[JsonSerializable(typeof(AriaVersionInfo))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(string[]))]
public partial class AriaJsonContext : JsonSerializerContext
{
}
