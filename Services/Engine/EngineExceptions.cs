using System;

namespace AriaUI.Services.Engine;

public class EngineException : Exception
{
    public EngineException(string message) : base(message) { }
    public EngineException(string message, Exception? innerException) : base(message, innerException) { }
}

public sealed class EngineNotReadyException : EngineException
{
    public EngineState CurrentState { get; }

    public EngineNotReadyException(EngineState currentState, string message)
        : base(message)
    {
        CurrentState = currentState;
    }
}

public sealed class EngineQueueFullException : EngineException
{
    public EngineQueueFullException(string message) : base(message) { }
}

public sealed class EngineFaultedException : EngineException
{
    public EngineFaultedException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class EngineCommandException : EngineException
{
    public string OperationName { get; }
    public long OperationId { get; }
    public string? Gid { get; }
    public int ErrorCode { get; }

    public EngineCommandException(
        string operationName,
        long operationId,
        string? gid,
        int errorCode,
        string message)
        : base($"[{operationName}#{operationId}] Gid: {gid ?? "N/A"}, Code: {errorCode}, Reason: {message}")
    {
        OperationName = operationName;
        OperationId = operationId;
        Gid = gid;
        ErrorCode = errorCode;
    }
}
