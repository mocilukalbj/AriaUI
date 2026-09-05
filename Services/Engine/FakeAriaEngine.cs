using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AriaUI.Models;

namespace AriaUI.Services.Engine;

/// <summary>
/// Controllable in-memory fake implementation of IAriaEngine for contract and lifecycle testing (C01-C08).
/// Enforces single owner thread, bounded channel capacity, monotonic request IDs, and fail-fast semantics.
/// </summary>
public sealed class FakeAriaEngine : IAriaEngine
{
    private abstract class CommandBase
    {
        public long OperationId { get; }
        public string OperationName { get; }
        public bool IsMutating { get; }
        public string? TargetGid { get; }
        public CancellationToken CancellationToken { get; }
        protected readonly FakeAriaEngine Engine;
        private int _started;

        protected CommandBase(FakeAriaEngine engine, long opId, string opName, bool isMutating, string? targetGid, CancellationToken ct)
        {
            Engine = engine;
            OperationId = opId;
            OperationName = opName;
            IsMutating = isMutating;
            TargetGid = targetGid;
            CancellationToken = ct;
        }

        public bool MarkStarted() => Interlocked.CompareExchange(ref _started, 1, 0) == 0;
        public bool IsStarted => Volatile.Read(ref _started) == 1;

        public abstract void Execute();
        public abstract void FailWith(Exception ex);
        public abstract void Cancel();
    }

    private sealed class Command<TResult> : CommandBase
    {
        private readonly Func<FakeAriaEngine, CancellationToken, TResult> _handler;
        private readonly TaskCompletionSource<TResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Command(FakeAriaEngine engine, long opId, string opName, bool isMutating, string? targetGid, Func<FakeAriaEngine, CancellationToken, TResult> handler, CancellationToken ct)
            : base(engine, opId, opName, isMutating, targetGid, ct)
        {
            _handler = handler;
        }

        public Task<TResult> Task => _tcs.Task;

        public override void Execute()
        {
            try
            {
                var result = _handler(Engine, CancellationToken);
                string? gid = TargetGid ?? (result is string s && s.Length == 16 ? s : null);
                Engine.RecordOutcome(new OperationOutcome
                {
                    OperationId = OperationId,
                    OperationName = OperationName,
                    Status = OperationStatus.Completed,
                    Gid = gid,
                    Timestamp = DateTime.UtcNow
                });
                _tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                Engine.RecordOutcome(new OperationOutcome
                {
                    OperationId = OperationId,
                    OperationName = OperationName,
                    Status = OperationStatus.Failed,
                    Gid = TargetGid,
                    ErrorMessage = "Operation canceled.",
                    Timestamp = DateTime.UtcNow
                });
                _tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                int? errCode = (ex as EngineCommandException)?.ErrorCode;
                Engine.RecordOutcome(new OperationOutcome
                {
                    OperationId = OperationId,
                    OperationName = OperationName,
                    Status = OperationStatus.Failed,
                    Gid = TargetGid,
                    ErrorMessage = ex.Message,
                    ErrorCode = errCode,
                    Timestamp = DateTime.UtcNow
                });
                _tcs.TrySetException(ex);
            }
        }

        public override void FailWith(Exception ex)
        {
            int? errCode = (ex as EngineCommandException)?.ErrorCode;
            Engine.RecordOutcome(new OperationOutcome
            {
                OperationId = OperationId,
                OperationName = OperationName,
                Status = OperationStatus.Failed,
                Gid = TargetGid,
                ErrorMessage = ex.Message,
                ErrorCode = errCode,
                Timestamp = DateTime.UtcNow
            });
            _tcs.TrySetException(ex);
        }

        public override void Cancel()
        {
            Engine.RecordOutcome(new OperationOutcome
            {
                OperationId = OperationId,
                OperationName = OperationName,
                Status = OperationStatus.Failed,
                Gid = TargetGid,
                ErrorMessage = "Operation canceled before execution.",
                Timestamp = DateTime.UtcNow
            });
            _tcs.TrySetCanceled(CancellationToken.IsCancellationRequested ? CancellationToken : CancellationToken.None);
        }
    }

    private static void SetOrReplaceOption(List<KeyValuePair<string, string>> list, string key, string value)
    {
        list.RemoveAll(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
        list.Add(new KeyValuePair<string, string>(key, value));
    }

    private static void ApplyOptions(List<KeyValuePair<string, string>> target, IEnumerable<KeyValuePair<string, string>> source)
    {
        foreach (var kv in source)
        {
            if (string.Equals(kv.Key, "header", StringComparison.OrdinalIgnoreCase))
            {
                target.Add(kv);
            }
            else
            {
                SetOrReplaceOption(target, kv.Key, kv.Value);
            }
        }
    }

    private readonly object _stateLock = new();
    private readonly Channel<CommandBase> _commandChannel;
    private readonly Channel<EngineEvent> _eventChannel;
    private readonly List<CommandBase> _acceptedCommands = new();
    private readonly Dictionary<string, AriaTaskInfo> _tasks = new();
    private readonly Dictionary<string, List<KeyValuePair<string, string>>> _taskOptions = new();
    private readonly List<KeyValuePair<string, string>> _globalOptions = new();
    private readonly ConcurrentDictionary<long, OperationOutcome> _operationOutcomes = new();
    private readonly ConcurrentQueue<long> _outcomeOrder = new();
    private readonly Thread _ownerThread;

    private long _operationIdCounter;
    private long _gidCounter;
    private long _eventSequenceCounter;
    private long _snapshotRevision;
    private TaskCompletionSource? _startTcs;
    private TaskCompletionSource? _shutdownTcs;
    private CancellationTokenSource? _ownerCts;
    private DateTime _shutdownDeadline = DateTime.MaxValue;
    private int _isDisposed;

    public EngineState State { get; private set; } = EngineState.Created;
    public string HostInstanceId { get; } = Guid.NewGuid().ToString("N");
    public EngineRuntimeConfig RuntimeConfig { get; }
    public EngineSnapshot CurrentSnapshot { get; private set; }

    public event EventHandler<EngineState>? StateChanged;
    public event EventHandler<EngineSnapshot>? SnapshotUpdated;

    // Test controllability hooks
    public Func<Task>? OnStartingHook { get; set; }
    public Action<long, string>? OnBeforeCommandExecute { get; set; }
    public bool InjectFatalOnNextCommand { get; set; }
    public Exception? FatalException { get; private set; }
    public int ExecutedCommandCount { get; private set; }

    public FakeAriaEngine(EngineRuntimeConfig? config = null)
    {
        RuntimeConfig = config ?? new EngineRuntimeConfig();
        RuntimeConfig.Validate();

        _commandChannel = Channel.CreateBounded<CommandBase>(
            new BoundedChannelOptions(RuntimeConfig.CommandQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        _eventChannel = Channel.CreateBounded<EngineEvent>(
            new BoundedChannelOptions(RuntimeConfig.KeyEventQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });

        CurrentSnapshot = new EngineSnapshot
        {
            HostInstanceId = HostInstanceId,
            Revision = 0,
            Timestamp = DateTime.UtcNow,
            GlobalStat = new AriaGlobalStat(),
            ActiveTasks = Array.Empty<AriaTaskInfo>(),
            WaitingTasks = Array.Empty<AriaTaskInfo>(),
            StoppedTasks = Array.Empty<AriaTaskInfo>()
        };

        _ownerThread = new Thread(OwnerThreadLoop)
        {
            Name = $"FakeEngineOwner-{HostInstanceId[..6]}",
            IsBackground = true
        };
    }

    private void SetState(EngineState newState)
    {
        lock (_stateLock)
        {
            if (State == newState) return;
            State = newState;
        }
        StateChanged?.Invoke(this, newState);
    }

    public Task StartAsync(EngineStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        lock (_stateLock)
        {
            if (State == EngineState.Ready)
            {
                return Task.CompletedTask;
            }
            if (State == EngineState.Starting)
            {
                return _startTcs?.Task ?? Task.CompletedTask;
            }
            if (State is EngineState.Stopped or EngineState.Faulted)
            {
                throw new EngineNotReadyException(State, $"Engine has terminated with state {State} and cannot be restarted.");
            }

            _globalOptions.Clear();
            if (options.InitialOptions != null)
            {
                ApplyOptions(_globalOptions, options.InitialOptions);
            }
            SetOrReplaceOption(_globalOptions, "dir", options.DownloadDir);
            SetOrReplaceOption(_globalOptions, "save-session", options.SessionFilePath);
            SetOrReplaceOption(_globalOptions, "save-session-interval", options.SaveSessionIntervalSeconds.ToString());

            SetState(EngineState.Starting);
            _startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ownerCts = new CancellationTokenSource();
            _ownerThread.Start();
        }

        return _startTcs.Task.WaitAsync(cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource shutdownTcs;
        lock (_stateLock)
        {
            if (State == EngineState.Created)
            {
                SetState(EngineState.Stopped);
                return;
            }
            if (State is EngineState.Stopped or EngineState.Faulted)
            {
                return;
            }
            if (State == EngineState.Stopping)
            {
                shutdownTcs = _shutdownTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                var previousState = State;
                SetState(EngineState.Stopping);
                _shutdownDeadline = DateTime.UtcNow + RuntimeConfig.ShutdownTimeout;
                _shutdownTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                shutdownTcs = _shutdownTcs;

                if (previousState == EngineState.Starting)
                {
                    // Starting 中关闭：记录停止请求，不再发布 Ready，启动请求以关闭结束 (Section 4.1)
                    _startTcs?.TrySetException(new OperationCanceledException("Start aborted by shutdown."));
                    _ownerCts?.Cancel();
                }
                else
                {
                    _commandChannel.Writer.TryComplete();
                }
            }
        }

        await shutdownTcs.Task.WaitAsync(RuntimeConfig.ShutdownTimeout + TimeSpan.FromSeconds(2), cancellationToken);
    }

    private void OwnerThreadLoop()
    {
        try
        {
            if (OnStartingHook != null)
            {
                OnStartingHook().GetAwaiter().GetResult();
            }

            lock (_stateLock)
            {
                if (State == EngineState.Stopping)
                {
                    SetState(EngineState.Stopped);
                    _shutdownTcs?.TrySetResult();
                    return;
                }
                SetState(EngineState.Ready);
                _startTcs?.TrySetResult();
            }

            PublishEvent(EngineEventType.StateChanged, null, "Engine Ready");

            var reader = _commandChannel.Reader;
            while (true)
            {
                if (_ownerCts?.IsCancellationRequested == true)
                {
                    break;
                }

                lock (_stateLock)
                {
                    if (State == EngineState.Stopping)
                    {
                        if (reader.Count == 0)
                        {
                            break;
                        }

                        if (DateTime.UtcNow >= _shutdownDeadline)
                        {
                            // 关闭期限到达：未开始的命令以关闭超时结束 (BOUNDARIES.md §7 / §4.3 C04)
                            while (reader.TryRead(out var unstartedCmd))
                            {
                                unstartedCmd.FailWith(new TimeoutException("Command rejected due to engine shutdown timeout."));
                            }
                            break;
                        }
                    }
                    if (State == EngineState.Faulted)
                    {
                        break;
                    }
                }

                // Batch processing within budget
                var budget = RuntimeConfig.BatchCommandBudget;
                var batchSw = System.Diagnostics.Stopwatch.StartNew();
                while (budget > 0 && batchSw.Elapsed < RuntimeConfig.BatchTimeBudget && reader.TryRead(out var cmd))
                {
                    lock (_stateLock)
                    {
                        if (State == EngineState.Stopping && DateTime.UtcNow >= _shutdownDeadline)
                        {
                            // 批处理期间已超期限：当次及后续未开始命令全部以关闭超时结束
                            cmd.FailWith(new TimeoutException("Command rejected due to engine shutdown timeout."));
                            while (reader.TryRead(out var unstartedCmd))
                            {
                                unstartedCmd.FailWith(new TimeoutException("Command rejected due to engine shutdown timeout."));
                            }
                            break;
                        }
                    }

                    budget--;
                    ExecuteOneCommand(cmd);
                }

                // Idle poll simulation
                if (reader.WaitToReadAsync(_ownerCts?.Token ?? CancellationToken.None).AsTask().Wait(RuntimeConfig.LoopTimeout))
                {
                    // More items available, loop
                }
                else
                {
                    // Heartbeat snapshot update if needed
                    UpdateSnapshotCore();
                }
            }
        }
        catch (OperationCanceledException) when (State == EngineState.Stopping)
        {
            // Expected during stopping
        }
        catch (Exception fatal)
        {
            TransitionToFaulted(fatal);
        }
        finally
        {
            while (_commandChannel.Reader.TryRead(out var remainingCmd))
            {
                remainingCmd.FailWith(new OperationCanceledException("Engine stopped."));
            }

            lock (_stateLock)
            {
                if (State != EngineState.Faulted)
                {
                    SetState(EngineState.Stopped);
                }
                _shutdownTcs?.TrySetResult();
            }
            PublishEvent(EngineEventType.StateChanged, null, $"Engine {State}");
        }
    }

    private void ExecuteOneCommand(CommandBase cmd)
    {
        ExecutedCommandCount++;

        if (InjectFatalOnNextCommand)
        {
            InjectFatalOnNextCommand = false;
            var fatalEx = new InvalidOperationException("Injected fatal native crash/overflow.");
            TransitionToFaulted(fatalEx);
            cmd.FailWith(new EngineFaultedException("Fatal native integrity loss.", fatalEx));
            return;
        }

        if (cmd.CancellationToken.IsCancellationRequested)
        {
            cmd.Cancel();
            return;
        }

        cmd.MarkStarted();
        OnBeforeCommandExecute?.Invoke(cmd.OperationId, cmd.OperationName);

        cmd.Execute();
        if (cmd.IsMutating)
        {
            UpdateSnapshotCore();
        }
    }

    private void TransitionToFaulted(Exception fatalEx)
    {
        lock (_stateLock)
        {
            FatalException = fatalEx;
            SetState(EngineState.Faulted);
            _commandChannel.Writer.TryComplete(fatalEx);
            _startTcs?.TrySetException(new EngineFaultedException("Engine faulted.", fatalEx));
        }

        // Drain any remaining unstarted commands with Faulted
        while (_commandChannel.Reader.TryRead(out var pending))
        {
            pending.FailWith(new EngineFaultedException("Engine faulted, unexecuted command rejected.", fatalEx));
        }

        PublishEvent(EngineEventType.Faulted, null, fatalEx.Message);
    }

    internal void RecordOutcome(OperationOutcome outcome)
    {
        _operationOutcomes[outcome.OperationId] = outcome;
        _outcomeOrder.Enqueue(outcome.OperationId);

        while (_outcomeOrder.Count > RuntimeConfig.GatewayRequestRecordCapacity && _outcomeOrder.TryDequeue(out var oldId))
        {
            if (_operationOutcomes.TryGetValue(oldId, out var oldOutcome) && oldOutcome.Status != OperationStatus.Pending)
            {
                _operationOutcomes.TryRemove(oldId, out _);
            }
        }
    }

    private Task<TResult> EnqueueCommand<TResult>(
        string opName,
        bool isMutating,
        string? targetGid,
        Func<FakeAriaEngine, CancellationToken, TResult> handler,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (State == EngineState.Faulted)
                throw new EngineFaultedException("Engine is in Faulted state.", FatalException);
            if (State != EngineState.Ready)
                throw new EngineNotReadyException(State, $"Engine is in {State} state, cannot accept '{opName}'.");
        }

        var opId = Interlocked.Increment(ref _operationIdCounter);
        RecordOutcome(new OperationOutcome
        {
            OperationId = opId,
            OperationName = opName,
            Status = OperationStatus.Pending,
            Gid = targetGid,
            Timestamp = DateTime.UtcNow
        });

        var cmd = new Command<TResult>(this, opId, opName, isMutating, targetGid, handler, cancellationToken);

        // Non-blocking write check to enforce capacity limit and fast rejection (D06)
        if (!_commandChannel.Writer.TryWrite(cmd))
        {
            RecordOutcome(new OperationOutcome
            {
                OperationId = opId,
                OperationName = opName,
                Status = OperationStatus.Failed,
                ErrorMessage = $"Engine command queue reached capacity ({RuntimeConfig.CommandQueueCapacity}).",
                Timestamp = DateTime.UtcNow
            });
            throw new EngineQueueFullException($"Engine command queue reached capacity ({RuntimeConfig.CommandQueueCapacity}).");
        }

        return cmd.Task;
    }

    public Task<string> AddUriAsync(
        IReadOnlyList<string> uris,
        IEnumerable<KeyValuePair<string, string>>? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uris);
        if (uris.Count == 0) throw new ArgumentException("uris cannot be empty.", nameof(uris));
        AriaOptionValidator.ValidateAll(options);

        var optionsList = options?.ToList();
        return EnqueueCommand("AddUri", isMutating: true, targetGid: null, (engine, ct) =>
        {
            var gid = Interlocked.Increment(ref engine._gidCounter).ToString("x16");
            var task = new AriaTaskInfo
            {
                Gid = gid,
                Status = "active",
                TotalLength = "1048576",
                CompletedLength = "0",
                DownloadSpeed = "102400"
            };
            engine._tasks[gid] = task;
            if (optionsList != null)
            {
                var taskOpts = new List<KeyValuePair<string, string>>();
                ApplyOptions(taskOpts, optionsList);
                engine._taskOptions[gid] = taskOpts;
            }
            engine.PublishEvent(EngineEventType.DownloadStart, gid, "Download started");
            return gid;
        }, cancellationToken);
    }

    public Task<string> AddTorrentAsync(
        string torrentFilePath,
        IEnumerable<KeyValuePair<string, string>>? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(torrentFilePath))
            throw new ArgumentException("torrentFilePath cannot be empty.", nameof(torrentFilePath));
        AriaOptionValidator.ValidateAll(options);

        var optionsList = options?.ToList();
        return EnqueueCommand("AddTorrent", isMutating: true, targetGid: null, (engine, ct) =>
        {
            var gid = Interlocked.Increment(ref engine._gidCounter).ToString("x16");
            var task = new AriaTaskInfo
            {
                Gid = gid,
                Status = "active",
                TotalLength = "2097152",
                CompletedLength = "0",
                DownloadSpeed = "512000"
            };
            engine._tasks[gid] = task;
            if (optionsList != null)
            {
                var taskOpts = new List<KeyValuePair<string, string>>();
                ApplyOptions(taskOpts, optionsList);
                engine._taskOptions[gid] = taskOpts;
            }
            engine.PublishEvent(EngineEventType.DownloadStart, gid, "Torrent download started");
            return gid;
        }, cancellationToken);
    }

    public Task PauseAsync(string gid, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("Pause", isMutating: true, targetGid: gid, (engine, ct) =>
        {
            if (!engine._tasks.TryGetValue(gid, out var task))
            {
                throw new EngineCommandException("Pause", 0, gid, 1, $"Task {gid} not found");
            }
            task.Status = "paused";
            engine.PublishEvent(EngineEventType.DownloadPause, gid, "Download paused");
            return true;
        }, cancellationToken);
    }

    public Task ResumeAsync(string gid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("Resume", isMutating: true, targetGid: gid, (engine, ct) =>
        {
            if (!engine._tasks.TryGetValue(gid, out var task))
            {
                throw new EngineCommandException("Resume", 0, gid, 1, $"Task {gid} not found");
            }
            task.Status = "active";
            engine.PublishEvent(EngineEventType.DownloadStart, gid, "Download resumed");
            return true;
        }, cancellationToken);
    }

    public Task RemoveAsync(string gid, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("Remove", isMutating: true, targetGid: gid, (engine, ct) =>
        {
            if (!engine._tasks.TryGetValue(gid, out var task))
            {
                throw new EngineCommandException("Remove", 0, gid, 1, $"Task {gid} not found");
            }
            task.Status = "removed";
            engine.PublishEvent(EngineEventType.DownloadStop, gid, "Download removed");
            return true;
        }, cancellationToken);
    }

    public Task PurgeDownloadResultAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueCommand("PurgeDownloadResult", isMutating: true, targetGid: null, (engine, ct) =>
        {
            var keysToRemove = engine._tasks
                .Where(kv => kv.Value.Status is "complete" or "error" or "removed")
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in keysToRemove)
            {
                engine._tasks.Remove(k);
            }
            return keysToRemove.Count;
        }, cancellationToken);
    }

    public Task ChangeOptionAsync(string gid, IEnumerable<KeyValuePair<string, string>> options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));
        AriaOptionValidator.ValidateAll(options);

        var optionsList = options.ToList();
        return EnqueueCommand("ChangeOption", isMutating: true, targetGid: gid, (engine, ct) =>
        {
            if (!engine._tasks.ContainsKey(gid))
            {
                throw new EngineCommandException("ChangeOption", 0, gid, 1, $"Task {gid} not found");
            }

            if (!engine._taskOptions.TryGetValue(gid, out var opts))
            {
                opts = new List<KeyValuePair<string, string>>();
                engine._taskOptions[gid] = opts;
            }
            ApplyOptions(opts, optionsList);
            return true;
        }, cancellationToken);
    }

    public Task ChangeGlobalOptionAsync(IEnumerable<KeyValuePair<string, string>> options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        AriaOptionValidator.ValidateAll(options);

        var optionsList = options.ToList();
        return EnqueueCommand("ChangeGlobalOption", isMutating: true, targetGid: null, (engine, ct) =>
        {
            ApplyOptions(engine._globalOptions, optionsList);
            return true;
        }, cancellationToken);
    }

    public Task<AriaOptionCollection> GetGlobalOptionAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueCommand("GetGlobalOption", isMutating: false, targetGid: null, (engine, ct) =>
        {
            return new AriaOptionCollection(engine._globalOptions);
        }, cancellationToken);
    }

    public Task<AriaOptionCollection> GetTaskOptionAsync(string gid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("GetTaskOption", isMutating: false, targetGid: gid, (engine, ct) =>
        {
            if (!engine._tasks.ContainsKey(gid))
            {
                throw new EngineCommandException("GetTaskOption", 0, gid, 1, $"Task {gid} not found");
            }

            var merged = new List<KeyValuePair<string, string>>(engine._globalOptions);
            if (engine._taskOptions.TryGetValue(gid, out var opts))
            {
                ApplyOptions(merged, opts);
            }
            return new AriaOptionCollection(merged);
        }, cancellationToken);
    }

    public Task<PagedResult<AriaTaskInfo>> GetTasksPagedAsync(
        TaskStatusFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");

        return EnqueueCommand("GetTasksPaged", isMutating: false, targetGid: null, (engine, ct) =>
        {
            IEnumerable<AriaTaskInfo> query = filter switch
            {
                TaskStatusFilter.Active => engine._tasks.Values.Where(t => t.Status == "active"),
                TaskStatusFilter.Waiting => engine._tasks.Values.Where(t => t.Status is "waiting" or "paused"),
                TaskStatusFilter.Stopped => engine._tasks.Values.Where(t => t.Status is "complete" or "error" or "removed"),
                _ => engine._tasks.Values
            };

            var list = query.OrderBy(t => t.Gid, StringComparer.Ordinal).ToList();
            var totalCount = list.Count;
            var page = list.Skip(offset).Take(limit).ToList();

            return new PagedResult<AriaTaskInfo>
            {
                Items = page,
                TotalCount = totalCount,
                Offset = offset,
                Limit = limit,
                Revision = Volatile.Read(ref engine._snapshotRevision)
            };
        }, cancellationToken);
    }

    public Task<OperationOutcome> GetOperationOutcomeAsync(long operationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_operationOutcomes.TryGetValue(operationId, out var outcome))
        {
            return Task.FromResult(outcome);
        }

        return Task.FromResult(new OperationOutcome
        {
            OperationId = operationId,
            OperationName = "Unknown",
            Status = OperationStatus.Unknown,
            Timestamp = DateTime.UtcNow
        });
    }

    public async IAsyncEnumerable<EngineEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = _eventChannel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    private void PublishEvent(EngineEventType type, string? gid, string? message)
    {
        var seq = Interlocked.Increment(ref _eventSequenceCounter);
        var evt = new EngineEvent(seq, type, gid, message, DateTime.UtcNow);
        _eventChannel.Writer.TryWrite(evt);
    }

    private void UpdateSnapshotCore()
    {
        var revision = Interlocked.Increment(ref _snapshotRevision);
        var active = new List<AriaTaskInfo>();
        var waiting = new List<AriaTaskInfo>();
        var stopped = new List<AriaTaskInfo>();

        foreach (var task in _tasks.Values)
        {
            switch (task.Status)
            {
                case "active": active.Add(task); break;
                case "waiting":
                case "paused": waiting.Add(task); break;
                default: stopped.Add(task); break;
            }
        }

        var snapshot = new EngineSnapshot
        {
            HostInstanceId = HostInstanceId,
            Revision = revision,
            Timestamp = DateTime.UtcNow,
            GlobalStat = new AriaGlobalStat
            {
                NumActive = active.Count.ToString(),
                NumWaiting = waiting.Count.ToString(),
                NumStopped = stopped.Count.ToString()
            },
            ActiveTasks = active,
            WaitingTasks = waiting,
            StoppedTasks = stopped
        };

        CurrentSnapshot = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            throw new ObjectDisposedException(nameof(FakeAriaEngine));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            await ShutdownAsync(CancellationToken.None);
        }
        catch
        {
            // Suppress exceptions during disposal
        }
        _ownerCts?.Dispose();
    }
}
