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
