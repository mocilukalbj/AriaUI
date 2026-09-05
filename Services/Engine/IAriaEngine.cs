using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;

namespace AriaUI.Services.Engine;

/// <summary>
/// Lifecycle states of the AriaEngine, conforming to D04 and Section 4.1.
/// Normal: Created -> Starting -> Ready -> Stopping -> Stopped.
/// Fatal error in Starting, Ready, Stopping -> Faulted.
/// </summary>
public enum EngineState
{
    Created = 0,
    Starting = 1,
    Ready = 2,
    Stopping = 3,
    Stopped = 4,
    Faulted = 5
}

/// <summary>
/// Start options for IAriaEngine. Explicitly specifies persistence path and interval (D04, Section 3).
/// </summary>
public sealed record EngineStartOptions
{
    public required string SessionFilePath { get; init; }
    public int SaveSessionIntervalSeconds { get; init; } = 30;
    public required string DownloadDir { get; init; }
    public IEnumerable<KeyValuePair<string, string>>? InitialOptions { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SessionFilePath))
            throw new ArgumentException("SessionFilePath cannot be empty.", nameof(SessionFilePath));
        if (SaveSessionIntervalSeconds <= 0)
            throw new ArgumentException("SaveSessionIntervalSeconds must be positive.", nameof(SaveSessionIntervalSeconds));
        if (string.IsNullOrWhiteSpace(DownloadDir))
            throw new ArgumentException("DownloadDir cannot be empty.", nameof(DownloadDir));
        AriaOptionValidator.ValidateAll(InitialOptions);
    }
}

/// <summary>
/// Bounded capacity and scheduling configuration for the engine (D06, Section 4.2, Section 6, BOUNDARIES.md).
/// Single source of truth for runtime constants.
/// </summary>
public sealed record EngineRuntimeConfig
{
    public int CommandQueueCapacity { get; init; } = 256;
    public int BatchCommandBudget { get; init; } = 32;
    public TimeSpan BatchTimeBudget { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan LoopTimeout { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>
    /// Native RUN_ONCE polling timeout (measured as ~1000ms by Phase 1 probe P01).
    /// Pure idle loop relies on epoll blocking with 0.00% CPU overhead; active tasks wake immediately on IO.
    /// </summary>
    public TimeSpan NativePollTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public int KeyEventQueueCapacity { get; init; } = 128;
    public int MaxGatewayConnections { get; init; } = 8;
    public int MaxAddRequestsPerSecond { get; init; } = 10;
    public int GatewayRequestRecordCapacity { get; init; } = 1024;
    public TimeSpan GatewayRecordRetention { get; init; } = TimeSpan.FromMinutes(10);
    public int GatewayMaxFrameSizeBytes { get; init; } = 64 * 1024;
    public TimeSpan GatewayInitialTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (CommandQueueCapacity is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(CommandQueueCapacity), "CommandQueueCapacity must be between 1 and 65536.");
        if (BatchCommandBudget is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(BatchCommandBudget), "BatchCommandBudget must be between 1 and 1024.");
        if (BatchTimeBudget <= TimeSpan.Zero || BatchTimeBudget > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(BatchTimeBudget), "BatchTimeBudget must be between 1ms and 10s.");
        if (LoopTimeout <= TimeSpan.Zero || LoopTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(LoopTimeout), "LoopTimeout must be between 1ms and 10s.");
        if (NativePollTimeout <= TimeSpan.Zero || NativePollTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(NativePollTimeout), "NativePollTimeout must be between 1ms and 10s.");
        if (ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout), "ShutdownTimeout must be between 1ms and 2m.");
        if (SnapshotInterval <= TimeSpan.Zero || SnapshotInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(SnapshotInterval), "SnapshotInterval must be between 1ms and 10s.");
        if (KeyEventQueueCapacity is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(KeyEventQueueCapacity), "KeyEventQueueCapacity must be between 1 and 65536.");
        if (MaxGatewayConnections is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxGatewayConnections), "MaxGatewayConnections must be between 1 and 64.");
        if (MaxAddRequestsPerSecond is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(MaxAddRequestsPerSecond), "MaxAddRequestsPerSecond must be between 1 and 1000.");
        if (GatewayRequestRecordCapacity is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(GatewayRequestRecordCapacity), "GatewayRequestRecordCapacity must be between 1 and 65536.");
        if (GatewayMaxFrameSizeBytes is < 1024 or > 10 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(GatewayMaxFrameSizeBytes), "GatewayMaxFrameSizeBytes must be between 1KB and 10MB.");
    }
}

/// <summary>
/// Status filter for paged task queries (F05).
/// </summary>
public enum TaskStatusFilter
{
    All = 0,
    Active = 1,
    Waiting = 2,
    Stopped = 3
}

/// <summary>
/// Paged query result with monotonic revision number to ensure view consistency across pages (F05, Section 3).
/// </summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required long Revision { get; init; }
}

/// <summary>
/// Status of an enqueued or executed engine operation (Section 6, Section 8.4, G08-G09).
/// </summary>
public enum OperationStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Unknown = 3
}

/// <summary>
/// Result/outcome record of an engine operation, decoupling caller timeout from engine completion.
/// </summary>
public sealed record OperationOutcome
{
    public required long OperationId { get; init; }
    public required string OperationName { get; init; }
    public required OperationStatus Status { get; init; }
    public string? Gid { get; init; }
    public string? ErrorMessage { get; init; }
    public int? ErrorCode { get; init; }
    public required DateTime Timestamp { get; init; }
}

/// <summary>
/// Snapshot of engine tasks and global stats with host instance ID and monotonic revision number (D06, Section 6).
/// </summary>
public sealed record EngineSnapshot
{
    public required string HostInstanceId { get; init; }
    public required long Revision { get; init; }
    public required DateTime Timestamp { get; init; }
    public required AriaGlobalStat GlobalStat { get; init; }
    public required IReadOnlyList<AriaTaskInfo> ActiveTasks { get; init; }
    public required IReadOnlyList<AriaTaskInfo> WaitingTasks { get; init; }
    public required IReadOnlyList<AriaTaskInfo> StoppedTasks { get; init; }
}

/// <summary>
/// Compact native engine events.
/// </summary>
public enum EngineEventType
{
    DownloadStart = 1,
    DownloadPause = 2,
    DownloadStop = 3,
    DownloadComplete = 4,
    DownloadError = 5,
    BtDownloadComplete = 6,
    StateChanged = 7,
    Faulted = 8
}

public sealed record EngineEvent(
    long SequenceNumber,
    EngineEventType Type,
    string? Gid,
    string? Message,
    DateTime Timestamp);

/// <summary>
/// Ordered collection of option key-value pairs that supports repeated keys (such as multiple headers),
/// case-insensitive lookup, and conversion to dictionary (D04, Section 3, Section 8.3).
/// </summary>
public sealed class AriaOptionCollection : IReadOnlyList<KeyValuePair<string, string>>
{
    public static readonly AriaOptionCollection Empty = new(Array.Empty<KeyValuePair<string, string>>());

    private readonly List<KeyValuePair<string, string>> _items;

    public AriaOptionCollection(IEnumerable<KeyValuePair<string, string>>? items = null)
    {
        _items = items != null ? new List<KeyValuePair<string, string>>(items) : new List<KeyValuePair<string, string>>();
    }

    public int Count => _items.Count;
    public KeyValuePair<string, string> this[int index] => _items[index];

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public string? this[string key]
    {
        get
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_items[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return _items[i].Value;
            }
            return null;
        }
    }

    public bool TryGetValue(string key, out string? value)
    {
        value = this[key];
        return value != null;
    }

    public bool ContainsKey(string key)
    {
        return _items.Any(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> GetValues(string key)
    {
        var list = new List<string>();
        foreach (var kv in _items)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(kv.Value);
            }
        }
        return list;
    }

    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _items)
        {
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }
}

/// <summary>
/// Defensive validator for options ensuring boundary safety (BOUNDARIES.md §3, §4).
/// Prevents null-byte truncation across C ABI and CRLF injection into headers.
/// </summary>
public static class AriaOptionValidator
{
    public const int MaxTextFieldLength = 8192; // BOUNDARIES.md §3: 单个文本字段上限 8 KiB

    public static void Validate(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Option key cannot be empty or whitespace.", nameof(key));

        if (key.Contains('\0') || key.Contains('\r') || key.Contains('\n'))
            throw new ArgumentException($"Option key '{key}' contains illegal control characters.", nameof(key));

        if (key.Length > MaxTextFieldLength)
            throw new ArgumentException($"Option key '{key}' exceeds 8 KiB limit.", nameof(key));

        if (value != null)
        {
            if (value.Contains('\0'))
                throw new ArgumentException($"Option '{key}' value contains null character.", nameof(value));

            if (string.Equals(key, "header", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Contains('\r') || value.Contains('\n'))
                    throw new ArgumentException($"Header option value contains illegal newline character.", nameof(value));
            }

            if (value.Length > MaxTextFieldLength)
                throw new ArgumentException($"Option '{key}' value exceeds 8 KiB limit.", nameof(value));
        }
    }

    public static void ValidateAll(IEnumerable<KeyValuePair<string, string>>? options)
    {
        if (options == null) return;
        foreach (var kv in options)
        {
            Validate(kv.Key, kv.Value);
        }
    }
}

/// <summary>
/// Managed interface for AriaEngine, abstracting native host and C ABI (Section 2, D02).
/// Supports repeated headers, paged queries, real option inspection, and operation outcomes.
/// </summary>
public interface IAriaEngine : IAsyncDisposable
{
    EngineState State { get; }
    string HostInstanceId { get; }
    EngineSnapshot CurrentSnapshot { get; }
    EngineRuntimeConfig RuntimeConfig { get; }

    event EventHandler<EngineState>? StateChanged;
    event EventHandler<EngineSnapshot>? SnapshotUpdated;

    Task StartAsync(EngineStartOptions options, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    // Task lifecycle and commands (options support multi-value/repeated headers via IEnumerable<KeyValuePair<string, string>>)
    Task<string> AddUriAsync(IReadOnlyList<string> uris, IEnumerable<KeyValuePair<string, string>>? options = null, CancellationToken cancellationToken = default);
    Task<string> AddTorrentAsync(string torrentFilePath, IEnumerable<KeyValuePair<string, string>>? options = null, CancellationToken cancellationToken = default);
    Task PauseAsync(string gid, bool force = false, CancellationToken cancellationToken = default);
    Task ResumeAsync(string gid, CancellationToken cancellationToken = default);
    Task RemoveAsync(string gid, bool force = false, CancellationToken cancellationToken = default);
    Task PurgeDownloadResultAsync(CancellationToken cancellationToken = default);

    // Options configuration & inspection (F04, §8.3)
    Task ChangeOptionAsync(string gid, IEnumerable<KeyValuePair<string, string>> options, CancellationToken cancellationToken = default);
    Task ChangeGlobalOptionAsync(IEnumerable<KeyValuePair<string, string>> options, CancellationToken cancellationToken = default);
    Task<AriaOptionCollection> GetGlobalOptionAsync(CancellationToken cancellationToken = default);
    Task<AriaOptionCollection> GetTaskOptionAsync(string gid, CancellationToken cancellationToken = default);

    // Paged task queries (F05)
    Task<PagedResult<AriaTaskInfo>> GetTasksPagedAsync(TaskStatusFilter filter, int offset, int limit, CancellationToken cancellationToken = default);

    // Operation outcome inspection (C07, G08-G09)
    Task<OperationOutcome> GetOperationOutcomeAsync(long operationId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<EngineEvent> WatchEventsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Backward-compatibility and convenience overloads for IAriaEngine.
/// </summary>
public static class AriaEngineExtensions
{
    public static Task<string> AddUriAsync(
        this IAriaEngine engine,
        IReadOnlyList<string> uris,
        IReadOnlyDictionary<string, string>? options,
        CancellationToken cancellationToken = default)
        => engine.AddUriAsync(uris, options, cancellationToken);

    public static Task<string> AddTorrentAsync(
        this IAriaEngine engine,
        string torrentFilePath,
        IReadOnlyDictionary<string, string>? options,
        CancellationToken cancellationToken = default)
        => engine.AddTorrentAsync(torrentFilePath, options, cancellationToken);

    public static Task ChangeOptionAsync(
        this IAriaEngine engine,
        string gid,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default)
        => engine.ChangeOptionAsync(gid, options, cancellationToken);

    public static Task ChangeGlobalOptionAsync(
        this IAriaEngine engine,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default)
        => engine.ChangeGlobalOptionAsync(options, cancellationToken);

    public static async Task<IReadOnlyDictionary<string, string>> GetGlobalOptionDictionaryAsync(
        this IAriaEngine engine,
        CancellationToken cancellationToken = default)
    {
        var opts = await engine.GetGlobalOptionAsync(cancellationToken);
        return opts.ToDictionary();
    }

    public static async Task<IReadOnlyDictionary<string, string>> GetTaskOptionDictionaryAsync(
        this IAriaEngine engine,
        string gid,
        CancellationToken cancellationToken = default)
    {
        var opts = await engine.GetTaskOptionAsync(gid, cancellationToken);
        return opts.ToDictionary();
    }
}
