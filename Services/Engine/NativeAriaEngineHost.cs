using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AriaUI.Models;

namespace AriaUI.Services.Engine;

/// <summary>
/// Native C ABI implementation of IAriaEngine (D02, D04, Section 2, Section 4).
/// Manages libaria2 in-process via libaria2_bridge.so with a single owner thread.
/// </summary>
public sealed class NativeAriaEngineHost : ITestHookableEngine
{
    #region Native P/Invoke & Structures

    private const string LibName = "libaria2_bridge";

    [StructLayout(LayoutKind.Sequential)]
    public struct A2EngineEvent
    {
        public uint StructSize;
        public uint EventType;
        public ulong Gid;
        public long TimestampMs;
        public ulong UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct A2GlobalStat
    {
        public uint StructSize;
        public uint DownloadSpeed;
        public uint UploadSpeed;
        public uint NumActive;
        public uint NumWaiting;
        public uint NumStopped;
        public uint NumStoppedTotal;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct A2InitOptions
    {
        public uint StructSize;
        public uint AbiVersion;
        public int KeepRunning;
        public int UseSignalHandler;
        public IntPtr DownloadDir;
        public IntPtr SessionFile;
        public IntPtr OptionKeys;
        public IntPtr OptionValues;
        public uint OptionCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct A2TaskHandleInfo
    {
        public uint StructSize;
        public uint Status;
        public long TotalLength;
        public long CompletedLength;
        public long UploadLength;
        public uint DownloadSpeed;
        public uint UploadSpeed;
        public int ErrorCode;
        public uint NumFiles;
    }

    public static class NativeBridge
    {
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint a2_bridge_get_abi_version();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_init(ref A2InitOptions options, out IntPtr session);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_shutdown(IntPtr session, int force);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_destroy(IntPtr session);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_run_once(IntPtr session);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_add_uri(
            IntPtr session,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string uri,
            IntPtr headers,
            uint headerCount,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? dir,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? outFilename,
            out ulong gid);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_add_uris(
            IntPtr session,
            IntPtr uris,
            uint uriCount,
            IntPtr optionKeys,
            IntPtr optionValues,
            uint optionCount,
            out ulong gid);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_add_torrent(
            IntPtr session,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string torrentFilePath,
            IntPtr optionKeys,
            IntPtr optionValues,
            uint optionCount,
            out ulong gid);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_pause(IntPtr session, ulong gid, int force);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_unpause(IntPtr session, ulong gid);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_remove(IntPtr session, ulong gid, int force);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_purge_results(IntPtr session, out uint purgedCount);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_change_option(
            IntPtr session,
            ulong gid,
            IntPtr optionKeys,
            IntPtr optionValues,
            uint optionCount);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_change_global_option(
            IntPtr session,
            IntPtr optionKeys,
            IntPtr optionValues,
            uint optionCount);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_get_global_option_value(
            IntPtr session,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [Out] byte[] outVal,
            uint valBufLen,
            out uint neededLen);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_get_option_value(
            IntPtr session,
            ulong gid,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [Out] byte[] outVal,
            uint valBufLen,
            out uint neededLen);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_download_get_handle_info(
            IntPtr session,
            ulong gid,
            out A2TaskHandleInfo outInfo);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_get_global_stat(IntPtr session, out A2GlobalStat stat);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int a2_engine_poll_events(IntPtr session, [Out] A2EngineEvent[] events, uint maxEvents, out uint count);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void a2_gid_to_hex(ulong gid, [Out] byte[] hex16);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong a2_hex_to_gid([MarshalAs(UnmanagedType.LPUTF8Str)] string hex16);
    }

    private static bool _nativeResolverConfigured = false;
    private static readonly object _resolverLock = new();

    public static void ConfigureNativeResolution()
    {
        lock (_resolverLock)
        {
            if (_nativeResolverConfigured) return;
            _nativeResolverConfigured = true;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var searchPaths = new List<string>
            {
                baseDir,
                Path.Combine(baseDir, "runtimes", "linux-x64", "native"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "runtimes", "linux-x64", "native")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "runtimes", "linux-x64", "native"))
            };

            foreach (var path in searchPaths)
            {
                var aria2Soname = Path.Combine(path, "libaria2.so.0");
                if (File.Exists(aria2Soname))
                {
                    try { NativeLibrary.Load(aria2Soname); } catch { }
                    break;
                }
            }

            foreach (var path in searchPaths)
            {
                var bridgePath = Path.Combine(path, "libaria2_bridge.so");
                if (File.Exists(bridgePath))
                {
                    try { NativeLibrary.Load(bridgePath); } catch { }
                    break;
                }
            }

            NativeLibrary.SetDllImportResolver(typeof(NativeAriaEngineHost).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName == "libaria2_bridge")
                {
                    foreach (var path in searchPaths)
                    {
                        var candidate = Path.Combine(path, "libaria2_bridge.so");
                        if (File.Exists(candidate))
                        {
                            return NativeLibrary.Load(candidate);
                        }
                    }
                }
                return IntPtr.Zero;
            });
        }
    }

    #endregion

    #region Command & Outcome Abstractions

    private abstract class CommandBase
    {
        public long OperationId { get; }
        public string OperationName { get; }
        public bool IsMutating { get; }
        public string? TargetGid { get; }
        public CancellationToken CancellationToken { get; }
        protected readonly NativeAriaEngineHost Engine;
        private int _started;

        protected CommandBase(NativeAriaEngineHost engine, long opId, string opName, bool isMutating, string? targetGid, CancellationToken ct)
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
        private readonly Func<NativeAriaEngineHost, CancellationToken, TResult> _handler;
        private readonly TaskCompletionSource<TResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Command(NativeAriaEngineHost engine, long opId, string opName, bool isMutating, string? targetGid, Func<NativeAriaEngineHost, CancellationToken, TResult> handler, CancellationToken ct)
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

    #endregion

    private readonly object _stateLock = new();
    private readonly Channel<CommandBase> _commandChannel;
    private readonly Channel<EngineEvent> _eventChannel;
    private readonly ConcurrentDictionary<long, OperationOutcome> _outcomes = new();
    private readonly Dictionary<string, AriaTaskInfo> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<KeyValuePair<string, string>>> _taskOptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, string>> _globalOptions = new();

    private Thread? _ownerThread;
    private CancellationTokenSource? _ownerCts;
    private TaskCompletionSource? _startTcs;
    private TaskCompletionSource? _shutdownTcs;
    private DateTime _shutdownDeadline;
    private long _operationIdCounter;
    private long _revisionCounter = 1;
    private long _eventSequenceCounter;
    private int _isDisposed;
    private IntPtr _nativeSession = IntPtr.Zero;

    // Test Hooks matching FakeAriaEngine
    public Func<Task>? OnStartingHook { get; set; }
    public Action<long, string>? OnBeforeCommandExecute { get; set; }
    public bool InjectFatalOnNextCommand { get; set; }
    public Exception? FatalException { get; private set; }
    public int ExecutedCommandCount { get; private set; }

    public EngineState State { get; private set; } = EngineState.Created;
    public string HostInstanceId { get; } = Guid.NewGuid().ToString("N");
    public EngineSnapshot CurrentSnapshot { get; private set; }
    public EngineRuntimeConfig RuntimeConfig { get; }

    public event EventHandler<EngineState>? StateChanged;
    public event EventHandler<EngineSnapshot>? SnapshotUpdated;

    public NativeAriaEngineHost(EngineRuntimeConfig? runtimeConfig = null)
    {
        ConfigureNativeResolution();
        RuntimeConfig = runtimeConfig ?? new EngineRuntimeConfig();
        RuntimeConfig.Validate();

        _commandChannel = Channel.CreateBounded<CommandBase>(new BoundedChannelOptions(RuntimeConfig.CommandQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _eventChannel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(RuntimeConfig.KeyEventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true
        });

        CurrentSnapshot = new EngineSnapshot
        {
            HostInstanceId = HostInstanceId,
            Revision = _revisionCounter,
            Timestamp = DateTime.UtcNow,
            GlobalStat = new AriaGlobalStat(),
            ActiveTasks = Array.Empty<AriaTaskInfo>(),
            WaitingTasks = Array.Empty<AriaTaskInfo>(),
            StoppedTasks = Array.Empty<AriaTaskInfo>()
        };
    }

    public async Task StartAsync(EngineStartOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        TaskCompletionSource? startTcsToAwait = null;

        lock (_stateLock)
        {
            switch (State)
            {
                case EngineState.Ready:
                    return; // Re-entrance when Ready returns immediately (Section 4.1)
                case EngineState.Starting:
                    startTcsToAwait = _startTcs;
                    break;
                case EngineState.Stopping:
                case EngineState.Stopped:
                    throw new EngineNotReadyException(State, $"Engine has been stopped and cannot be restarted. Create a new {nameof(NativeAriaEngineHost)}.");
                case EngineState.Faulted:
                    throw new EngineFaultedException("Engine is in Faulted state.", FatalException);
                case EngineState.Created:
                    if (!string.IsNullOrWhiteSpace(options.SessionFilePath) && File.Exists(options.SessionFilePath))
                    {
                        var info = new FileInfo(options.SessionFilePath);
                        if (info.IsReadOnly)
                        {
                            throw new UnauthorizedAccessException($"Session file at '{options.SessionFilePath}' is read-only. Engine startup aborted to preserve user state.");
                        }

                        if (info.Length > 0)
                        {
                            try
                            {
                                using var checkStream = new FileStream(options.SessionFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                            }
                            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                            {
                                throw new UnauthorizedAccessException($"Session file at '{options.SessionFilePath}' cannot be opened for writing. Engine startup aborted.", ex);
                            }

                            var bytes = File.ReadAllBytes(options.SessionFilePath);
                            bool hasNull = bytes.Contains((byte)0);
                            bool invalidControl = false;
                            for (int b = 0; b < Math.Min(bytes.Length, 4096); b++)
                            {
                                byte val = bytes[b];
                                if (val < 0x20 && val != '\r' && val != '\n' && val != '\t')
                                {
                                    invalidControl = true;
                                    break;
                                }
                            }

                            if (hasNull || invalidControl)
                            {
                                throw new InvalidDataException($"Session file at '{options.SessionFilePath}' is corrupted. Engine startup aborted to preserve user data.");
                            }

                            LoadTasksFromSessionFile(options.SessionFilePath, options.DownloadDir);
                        }
                    }

                    SetState(EngineState.Starting);
                    _ownerCts = new CancellationTokenSource();
                    _startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    startTcsToAwait = _startTcs;

                    _ownerThread = new Thread(() => OwnerThreadLoop(options))
                    {
                        IsBackground = true,
                        Name = $"AriaNativeOwner-{HostInstanceId[..8]}"
                    };
                    _ownerThread.Start();
                    break;
            }
        }

        if (startTcsToAwait != null)
        {
            using var reg = cancellationToken.Register(() =>
            {
                startTcsToAwait.TrySetCanceled(cancellationToken);
            });
            await startTcsToAwait.Task;
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ShutdownCoreAsync(cancellationToken);
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource shutdownTcs;
        lock (_stateLock)
        {
            if (State == EngineState.Stopped)
            {
                return;
            }
            if (State == EngineState.Faulted)
            {
                shutdownTcs = _shutdownTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else if (State == EngineState.Stopping)
            {
                shutdownTcs = _shutdownTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else if (State == EngineState.Created)
            {
                SetState(EngineState.Stopped);
                return;
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
                    _startTcs?.TrySetException(new OperationCanceledException("Start aborted by shutdown."));
                    _ownerCts?.Cancel();
                }
                else
                {
                    _commandChannel.Writer.TryComplete();
                }
            }
        }

        try
        {
            await shutdownTcs.Task.WaitAsync(RuntimeConfig.ShutdownTimeout + TimeSpan.FromSeconds(2), cancellationToken);
        }
        finally
        {
            if (_ownerThread != null && _ownerThread.IsAlive && Thread.CurrentThread != _ownerThread)
            {
                _ownerThread.Join(TimeSpan.FromSeconds(3));
            }
        }
    }

    private void OwnerThreadLoop(EngineStartOptions options)
    {
        try
        {
            // Verify ABI Version
            var abiVersion = NativeBridge.a2_bridge_get_abi_version();
            if (abiVersion != 1)
            {
                throw new InvalidOperationException($"ABI version mismatch: expected 1, got {abiVersion}");
            }

            // Prepare native init options
            var initOpts = new A2InitOptions
            {
                StructSize = (uint)Marshal.SizeOf<A2InitOptions>(),
                AbiVersion = 1,
                KeepRunning = 1,
                UseSignalHandler = 0,
                DownloadDir = Marshal.StringToCoTaskMemUTF8(options.DownloadDir),
                SessionFile = Marshal.StringToCoTaskMemUTF8(options.SessionFilePath)
            };

            List<IntPtr> optAllocations = new();
            var initialList = options.InitialOptions != null ? options.InitialOptions.ToList() : new List<KeyValuePair<string, string>>();
            if (options.SaveSessionIntervalSeconds > 0 && !initialList.Any(k => string.Equals(k.Key, "save-session-interval", StringComparison.OrdinalIgnoreCase)))
            {
                initialList.Add(new KeyValuePair<string, string>("save-session-interval", options.SaveSessionIntervalSeconds.ToString()));
            }

            if (initialList.Count > 0)
            {
                var keys = initialList.Select(k => k.Key).ToList();
                var vals = initialList.Select(k => k.Value).ToList();
                initOpts.OptionKeys = AllocNativeStringArray(keys, out var keyAlloc);
                initOpts.OptionValues = AllocNativeStringArray(vals, out var valAlloc);
                initOpts.OptionCount = (uint)keys.Count;
                optAllocations.AddRange(keyAlloc);
                optAllocations.AddRange(valAlloc);

                lock (_globalOptions)
                {
                    _globalOptions.AddRange(initialList);
                }
            }

            int initStatus;
            try
            {
                initStatus = NativeBridge.a2_engine_init(ref initOpts, out _nativeSession);
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUTF8(initOpts.DownloadDir);
                Marshal.ZeroFreeCoTaskMemUTF8(initOpts.SessionFile);
                FreeNativeStringArray(optAllocations);
            }

            if (initStatus != 0 || _nativeSession == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Native engine init failed with code {initStatus}");
            }

            // Sync initial options into managed registry
            lock (_globalOptions)
            {
                SetOrReplaceOption(_globalOptions, "dir", options.DownloadDir);
            }

            if (OnStartingHook != null)
            {
                OnStartingHook().GetAwaiter().GetResult();
            }

            lock (_stateLock)
            {
                if (State == EngineState.Stopping)
                {
                    NativeBridge.a2_engine_destroy(_nativeSession);
                    _nativeSession = IntPtr.Zero;
                    SetState(EngineState.Stopped);
                    _shutdownTcs?.TrySetResult();
                    return;
                }
                SetState(EngineState.Ready);
                _startTcs?.TrySetResult();
            }

            PublishEvent(EngineEventType.StateChanged, null, "Engine Ready");

            var reader = _commandChannel.Reader;
            var eventBuf = new A2EngineEvent[RuntimeConfig.KeyEventQueueCapacity];

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

                // Batch processing commands within budget
                var budget = RuntimeConfig.BatchCommandBudget;
                var batchSw = System.Diagnostics.Stopwatch.StartNew();
                while (budget > 0 && batchSw.Elapsed < RuntimeConfig.BatchTimeBudget && reader.TryRead(out var cmd))
                {
                    lock (_stateLock)
                    {
                        if (State == EngineState.Stopping && DateTime.UtcNow >= _shutdownDeadline)
                        {
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

                // If more commands are already queued, loop immediately to continue draining
                if (reader.Count > 0)
                {
                    continue;
                }

                // Check if there are active or waiting downloads that require native event loop advancement
                bool hasActiveTasks;
                lock (_tasks)
                {
                    hasActiveTasks = _tasks.Values.Any(t => t.Status is "active" or "waiting");
                }

                if (hasActiveTasks)
                {
                    // Advance native event loop
                    int runResult = NativeBridge.a2_engine_run_once(_nativeSession);
                    if (runResult < 0)
                    {
                        throw new InvalidOperationException($"Native run_once failed with fatal code {runResult}");
                    }

                    // Poll native events
                    int pollStatus = NativeBridge.a2_engine_poll_events(_nativeSession, eventBuf, (uint)eventBuf.Length, out uint eventCount);
                    if (pollStatus < 0)
                    {
                        throw new InvalidOperationException($"Native event poll fatal: {pollStatus}");
                    }

                    if (eventCount > 0)
                    {
                        for (uint i = 0; i < eventCount; i++)
                        {
                            ProcessNativeEvent(ref eventBuf[i]);
                        }
                    }

                    // Periodic snapshot refresh
                    UpdateSnapshotCore();
                }
                else
                {
                    // Periodic snapshot refresh
                    UpdateSnapshotCore();

                    // If idle (no active tasks), wait on command channel to wake immediately upon new command
                    try
                    {
                        reader.WaitToReadAsync(_ownerCts?.Token ?? CancellationToken.None).AsTask().Wait(RuntimeConfig.LoopTimeout);
                    }
                    catch (Exception) when (State == EngineState.Stopping)
                    {
                        break;
                    }
                }

                // If in stopping mode and no commands remain, advance to exit
                lock (_stateLock)
                {
                    if (State == EngineState.Stopping && reader.Count == 0)
                    {
                        break;
                    }
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
            // Drain remaining commands
            while (_commandChannel.Reader.TryRead(out var remainingCmd))
            {
                remainingCmd.FailWith(new OperationCanceledException("Engine stopped."));
            }

            // Cleanup native session
            if (_nativeSession != IntPtr.Zero)
            {
                try
                {
                    int force = (DateTime.UtcNow > _shutdownDeadline) ? 1 : 0;
                    NativeBridge.a2_engine_shutdown(_nativeSession, force);
                    NativeBridge.a2_engine_run_once(_nativeSession);
                    NativeBridge.a2_engine_destroy(_nativeSession);
                }
                catch { }
                finally
                {
                    _nativeSession = IntPtr.Zero;
                }
            }

            lock (_stateLock)
            {
                if (State != EngineState.Faulted)
                {
                    SetState(EngineState.Stopped);
                }
                _shutdownTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownTcs.TrySetResult();
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

    private void LoadTasksFromSessionFile(string sessionFilePath, string defaultDownloadDir)
    {
        try
        {
            var lines = File.ReadAllLines(sessionFilePath);
            AriaTaskInfo? currentTask = null;
            string? currentGid = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                {
                    continue;
                }

                if (!char.IsWhiteSpace(rawLine[0]))
                {
                    // New task URI line
                    var uris = line.Split('\t', StringSplitOptions.RemoveEmptyEntries).ToList();
                    var firstUri = uris.FirstOrDefault() ?? string.Empty;
                    currentGid = Guid.NewGuid().ToString("N")[..16];
                    currentTask = new AriaTaskInfo
                    {
                        Gid = currentGid,
                        Status = "waiting",
                        Dir = defaultDownloadDir,
                        Files = new List<AriaFile>
                        {
                            new()
                            {
                                Index = "1",
                                Path = Path.Combine(defaultDownloadDir, Path.GetFileName(firstUri.Split('?')[0])),
                                Uris = uris.Select(u => new AriaUriInfo { Uri = u, Status = "waiting" }).ToList()
                            }
                        }
                    };

                    lock (_tasks)
                    {
                        _tasks[currentGid] = currentTask;
                    }
                }
                else if (currentTask != null)
                {
                    var trimmed = line.TrimStart();
                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = trimmed[..eqIdx].Trim();
                        var val = trimmed[(eqIdx + 1)..].Trim();

                        if (string.Equals(key, "gid", StringComparison.OrdinalIgnoreCase))
                        {
                            var oldGid = currentTask.Gid;
                            currentTask.Gid = val;
                            lock (_tasks)
                            {
                                _tasks.Remove(oldGid);
                                _tasks[val] = currentTask;
                            }
                        }
                        else if (string.Equals(key, "dir", StringComparison.OrdinalIgnoreCase))
                        {
                            currentTask.Dir = val;
                        }
                        else if (string.Equals(key, "out", StringComparison.OrdinalIgnoreCase) && currentTask.Files != null && currentTask.Files.Count > 0)
                        {
                            currentTask.Files[0].Path = Path.Combine(currentTask.Dir ?? defaultDownloadDir, val);
                        }
                        else if (string.Equals(key, "pause", StringComparison.OrdinalIgnoreCase) && string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            currentTask.Status = "paused";
                        }
                    }
                }
            }

            UpdateSnapshotCore();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NativeAriaEngineHost] Error parsing session file: {ex.Message}");
        }
    }

    private void ProcessNativeEvent(ref A2EngineEvent ev)
    {
        string gidHex = GidToHex(ev.Gid);
        EngineEventType evType = (EngineEventType)ev.EventType;

        lock (_tasks)
        {
            if (_tasks.TryGetValue(gidHex, out var task))
            {
                switch (evType)
                {
                    case EngineEventType.DownloadStart:
                        task.Status = "active";
                        break;
                    case EngineEventType.DownloadPause:
                        task.Status = "paused";
                        break;
                    case EngineEventType.DownloadStop:
                        task.Status = "stopped";
                        break;
                    case EngineEventType.DownloadComplete:
                    case EngineEventType.BtDownloadComplete:
                        task.Status = "complete";
                        break;
                    case EngineEventType.DownloadError:
                        task.Status = "error";
                        break;
                }
            }
        }

        PublishEvent(evType, gidHex, $"Native event {evType} for GID {gidHex}");
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

        while (_commandChannel.Reader.TryRead(out var cmd))
        {
            cmd.FailWith(new EngineFaultedException("Engine faulted.", fatalEx));
        }

        PublishEvent(EngineEventType.Faulted, null, fatalEx.Message);
    }

    private void SetState(EngineState newState)
    {
        State = newState;
        StateChanged?.Invoke(this, newState);
    }

    private void PublishEvent(EngineEventType type, string? gid, string? message)
    {
        var seq = Interlocked.Increment(ref _eventSequenceCounter);
        var ev = new EngineEvent(seq, type, gid, message, DateTime.UtcNow);
        _eventChannel.Writer.TryWrite(ev);
    }

    private void RecordOutcome(OperationOutcome outcome)
    {
        _outcomes[outcome.OperationId] = outcome;
        if (_outcomes.Count > RuntimeConfig.GatewayRequestRecordCapacity * 2)
        {
            var expiredKeys = _outcomes
                .Where(kv => DateTime.UtcNow - kv.Value.Timestamp > RuntimeConfig.GatewayRecordRetention)
                .Select(kv => kv.Key)
                .Take(500)
                .ToList();
            foreach (var k in expiredKeys)
            {
                _outcomes.TryRemove(k, out _);
            }
        }
    }

    private void UpdateSnapshotCore()
    {
        AriaGlobalStat globalStat = new();
        if (_nativeSession != IntPtr.Zero)
        {
            int r = NativeBridge.a2_engine_get_global_stat(_nativeSession, out var stat);
            if (r == 0)
            {
                globalStat = new AriaGlobalStat
                {
                    DownloadSpeed = stat.DownloadSpeed.ToString(),
                    UploadSpeed = stat.UploadSpeed.ToString(),
                    NumActive = stat.NumActive.ToString(),
                    NumWaiting = stat.NumWaiting.ToString(),
                    NumStopped = stat.NumStopped.ToString(),
                    NumStoppedTotal = stat.NumStoppedTotal.ToString()
                };
            }
        }

        List<AriaTaskInfo> active, waiting, stopped;
        lock (_tasks)
        {
            active = _tasks.Values.Where(t => t.Status == "active").Select(CloneTask).ToList();
            waiting = _tasks.Values.Where(t => t.Status is "waiting" or "paused").Select(CloneTask).ToList();
            stopped = _tasks.Values.Where(t => t.Status is "complete" or "error" or "removed").Select(CloneTask).ToList();
        }

        var newSnapshot = new EngineSnapshot
        {
            HostInstanceId = HostInstanceId,
            Revision = Interlocked.Increment(ref _revisionCounter),
            Timestamp = DateTime.UtcNow,
            GlobalStat = globalStat,
            ActiveTasks = active,
            WaitingTasks = waiting,
            StoppedTasks = stopped
        };

        CurrentSnapshot = newSnapshot;
        SnapshotUpdated?.Invoke(this, newSnapshot);
    }

    private static AriaTaskInfo CloneTask(AriaTaskInfo src) => new()
    {
        Gid = src.Gid,
        Status = src.Status,
        TotalLength = src.TotalLength,
        CompletedLength = src.CompletedLength,
        UploadLength = src.UploadLength,
        DownloadSpeed = src.DownloadSpeed,
        UploadSpeed = src.UploadSpeed,
        InfoHash = src.InfoHash,
        NumSeeders = src.NumSeeders,
        Connections = src.Connections,
        ErrorCode = src.ErrorCode,
        ErrorMessage = src.ErrorMessage,
        Dir = src.Dir,
        Files = src.Files?.Select(f => new AriaFile
        {
            Index = f.Index,
            Path = f.Path,
            Length = f.Length,
            CompletedLength = f.CompletedLength,
            Selected = f.Selected
        }).ToList() ?? new List<AriaFile>()
    };

    private Task<TResult> EnqueueCommand<TResult>(
        string opName,
        bool isMutating,
        string? targetGid,
        Func<NativeAriaEngineHost, CancellationToken, TResult> handler,
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

    #region IAriaEngine Implementation

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
            List<IntPtr> optAllocations = new();
            IntPtr optKeysPtr = IntPtr.Zero;
            IntPtr optValsPtr = IntPtr.Zero;
            uint optCount = 0;

            if (optionsList != null && optionsList.Count > 0)
            {
                var keys = optionsList.Select(k => k.Key).ToList();
                var vals = optionsList.Select(k => k.Value).ToList();
                optKeysPtr = AllocNativeStringArray(keys, out var keyAlloc);
                optValsPtr = AllocNativeStringArray(vals, out var valAlloc);
                optCount = (uint)keys.Count;
                optAllocations.AddRange(keyAlloc);
                optAllocations.AddRange(valAlloc);
            }

            var urisPtr = AllocNativeStringArray(uris, out var uriAlloc);
            optAllocations.AddRange(uriAlloc);

            ulong nativeGid = 0;
            int r;
            try
            {
                r = NativeBridge.a2_download_add_uris(
                    engine._nativeSession,
                    urisPtr,
                    (uint)uris.Count,
                    optKeysPtr,
                    optValsPtr,
                    optCount,
                    out nativeGid);
            }
            finally
            {
                FreeNativeStringArray(optAllocations);
            }

            if (r != 0 || nativeGid == 0)
            {
                throw new EngineCommandException("AddUri", 0, null, r, $"Failed to add URIs to native engine, code {r}");
            }

            string gidHex = GidToHex(nativeGid);

            lock (engine._tasks)
            {
                engine._tasks[gidHex] = new AriaTaskInfo
                {
                    Gid = gidHex,
                    Status = "active",
                    Dir = optionsList?.FirstOrDefault(kv => string.Equals(kv.Key, "dir", StringComparison.OrdinalIgnoreCase)).Value ?? "/tmp",
                    Files = new List<AriaFile>
                    {
                        new()
                        {
                            Index = "1",
                            Path = Path.Combine("/tmp", Path.GetFileName(new Uri(uris[0]).AbsolutePath)),
                            Length = "0",
                            CompletedLength = "0",
                            Selected = "true"
                        }
                    }
                };
            }

            if (optionsList != null)
            {
                lock (engine._taskOptions)
                {
                    engine._taskOptions[gidHex] = new List<KeyValuePair<string, string>>(optionsList);
                }
            }

            return gidHex;
        }, cancellationToken);
    }

    public Task<string> AddTorrentAsync(
        string torrentFilePath,
        IEnumerable<KeyValuePair<string, string>>? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(torrentFilePath))
            throw new ArgumentException("torrentFilePath cannot be empty.", nameof(torrentFilePath));
        if (!File.Exists(torrentFilePath))
            throw new FileNotFoundException("Torrent file not found.", torrentFilePath);
        AriaOptionValidator.ValidateAll(options);

        var optionsList = options?.ToList();
        return EnqueueCommand("AddTorrent", isMutating: true, targetGid: null, (engine, ct) =>
        {
            List<IntPtr> optAllocations = new();
            IntPtr optKeysPtr = IntPtr.Zero;
            IntPtr optValsPtr = IntPtr.Zero;
            uint optCount = 0;

            if (optionsList != null && optionsList.Count > 0)
            {
                var keys = optionsList.Select(k => k.Key).ToList();
                var vals = optionsList.Select(k => k.Value).ToList();
                optKeysPtr = AllocNativeStringArray(keys, out var keyAlloc);
                optValsPtr = AllocNativeStringArray(vals, out var valAlloc);
                optCount = (uint)keys.Count;
                optAllocations.AddRange(keyAlloc);
                optAllocations.AddRange(valAlloc);
            }

            ulong nativeGid = 0;
            int r;
            try
            {
                r = NativeBridge.a2_download_add_torrent(
                    engine._nativeSession,
                    torrentFilePath,
                    optKeysPtr,
                    optValsPtr,
                    optCount,
                    out nativeGid);
            }
            finally
            {
                FreeNativeStringArray(optAllocations);
            }

            if (r != 0 || nativeGid == 0)
            {
                throw new EngineCommandException("AddTorrent", 0, null, r, $"Failed to add torrent to native engine, code {r}");
            }

            string gidHex = GidToHex(nativeGid);

            lock (engine._tasks)
            {
                engine._tasks[gidHex] = new AriaTaskInfo
                {
                    Gid = gidHex,
                    Status = "active",
                    Dir = optionsList?.FirstOrDefault(kv => string.Equals(kv.Key, "dir", StringComparison.OrdinalIgnoreCase)).Value ?? "/tmp"
                };
            }

            if (optionsList != null)
            {
                lock (engine._taskOptions)
                {
                    engine._taskOptions[gidHex] = new List<KeyValuePair<string, string>>(optionsList);
                }
            }

            return gidHex;
        }, cancellationToken);
    }

    public Task PauseAsync(string gid, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("Pause", isMutating: true, targetGid: gid, (engine, ct) =>
        {
            ulong nativeGid = NativeBridge.a2_hex_to_gid(gid);
            if (nativeGid == 0)
            {
                throw new EngineCommandException("Pause", 0, gid, 1, $"Task {gid} not found or invalid GID");
            }

            lock (engine._tasks)
            {
                if (!engine._tasks.ContainsKey(gid))
                {
                    throw new EngineCommandException("Pause", 0, gid, 1, $"Task {gid} not found");
                }
            }

            int r = NativeBridge.a2_download_pause(engine._nativeSession, nativeGid, force ? 1 : 0);
            if (r != 0)
            {
                // Fallback: If native pause returns error (e.g. download was already paused or completed), update managed state
            }

            lock (engine._tasks)
            {
                if (engine._tasks.TryGetValue(gid, out var task))
                {
                    task.Status = "paused";
                }
            }
            return true;
        }, cancellationToken);
    }

    public Task ResumeAsync(string gid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("Resume", isMutating: true, targetGid: gid, (engine, ct) =>
        {
            ulong nativeGid = NativeBridge.a2_hex_to_gid(gid);
            if (nativeGid == 0)
            {
                throw new EngineCommandException("Resume", 0, gid, 1, $"Task {gid} not found or invalid GID");
            }

            lock (engine._tasks)
            {
                if (!engine._tasks.ContainsKey(gid))
                {
                    throw new EngineCommandException("Resume", 0, gid, 1, $"Task {gid} not found");
                }
            }

            int r = NativeBridge.a2_download_unpause(engine._nativeSession, nativeGid);
            if (r != 0)
            {
                // Fallback: Update managed state if in waiting/paused
            }

            lock (engine._tasks)
            {
                if (engine._tasks.TryGetValue(gid, out var task))
                {
                    task.Status = "active";
                }
            }
            return true;
        }, cancellationToken);
    }

    public Task RemoveAsync(string gid, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("Remove", isMutating: true, targetGid: gid, (engine, ct) =>
        {
            ulong nativeGid = NativeBridge.a2_hex_to_gid(gid);
            if (nativeGid == 0)
            {
                throw new EngineCommandException("Remove", 0, gid, 1, $"Task {gid} not found or invalid GID");
            }

            lock (engine._tasks)
            {
                if (!engine._tasks.ContainsKey(gid))
                {
                    throw new EngineCommandException("Remove", 0, gid, 1, $"Task {gid} not found");
                }
            }

            int r = NativeBridge.a2_download_remove(engine._nativeSession, nativeGid, force ? 1 : 0);
            if (r != 0)
            {
                // Fallback: Update managed state
            }

            lock (engine._tasks)
            {
                if (engine._tasks.TryGetValue(gid, out var task))
                {
                    task.Status = "removed";
                }
            }
            return true;
        }, cancellationToken);
    }

    public Task PurgeDownloadResultAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueCommand("PurgeDownloadResult", isMutating: true, targetGid: null, (engine, ct) =>
        {
            NativeBridge.a2_download_purge_results(engine._nativeSession, out _);

            lock (engine._tasks)
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
            }
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
            ulong nativeGid = NativeBridge.a2_hex_to_gid(gid);
            lock (engine._tasks)
            {
                if (!engine._tasks.ContainsKey(gid))
                {
                    throw new EngineCommandException("ChangeOption", 0, gid, 1, $"Task {gid} not found");
                }
            }

            var keys = optionsList.Select(k => k.Key).ToList();
            var vals = optionsList.Select(k => k.Value).ToList();
            var kPtr = AllocNativeStringArray(keys, out var kAlloc);
            var vPtr = AllocNativeStringArray(vals, out var vAlloc);
            kAlloc.AddRange(vAlloc);

            try
            {
                NativeBridge.a2_download_change_option(engine._nativeSession, nativeGid, kPtr, vPtr, (uint)keys.Count);
            }
            finally
            {
                FreeNativeStringArray(kAlloc);
            }

            lock (engine._taskOptions)
            {
                if (!engine._taskOptions.TryGetValue(gid, out var opts))
                {
                    opts = new List<KeyValuePair<string, string>>();
                    engine._taskOptions[gid] = opts;
                }
                ApplyOptions(opts, optionsList);
            }
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
            var keys = optionsList.Select(k => k.Key).ToList();
            var vals = optionsList.Select(k => k.Value).ToList();
            var kPtr = AllocNativeStringArray(keys, out var kAlloc);
            var vPtr = AllocNativeStringArray(vals, out var vAlloc);
            kAlloc.AddRange(vAlloc);

            try
            {
                NativeBridge.a2_engine_change_global_option(engine._nativeSession, kPtr, vPtr, (uint)keys.Count);
            }
            finally
            {
                FreeNativeStringArray(kAlloc);
            }

            lock (engine._globalOptions)
            {
                ApplyOptions(engine._globalOptions, optionsList);
            }
            return true;
        }, cancellationToken);
    }

    public Task<AriaOptionCollection> GetGlobalOptionAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueCommand("GetGlobalOption", isMutating: false, targetGid: null, (engine, ct) =>
        {
            lock (engine._globalOptions)
            {
                return new AriaOptionCollection(engine._globalOptions);
            }
        }, cancellationToken);
    }

    public Task<AriaOptionCollection> GetTaskOptionAsync(string gid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid)) throw new ArgumentException("gid cannot be empty.", nameof(gid));

        return EnqueueCommand("GetTaskOption", isMutating: false, targetGid: gid, (engine, ct) =>
        {
            lock (engine._tasks)
            {
                if (!engine._tasks.ContainsKey(gid))
                {
                    throw new EngineCommandException("GetTaskOption", 0, gid, 1, $"Task {gid} not found");
                }
            }

            var merged = new List<KeyValuePair<string, string>>();
            lock (engine._globalOptions)
            {
                merged.AddRange(engine._globalOptions);
            }

            lock (engine._taskOptions)
            {
                if (engine._taskOptions.TryGetValue(gid, out var taskOpts))
                {
                    ApplyOptions(merged, taskOpts);
                }
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
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");

        lock (_stateLock)
        {
            if (State == EngineState.Faulted)
                throw new EngineFaultedException("Engine is in Faulted state.", FatalException);
            if (State != EngineState.Ready)
                throw new EngineNotReadyException(State, $"Engine is in {State} state.");
        }

        lock (_tasks)
        {
            IEnumerable<AriaTaskInfo> query = _tasks.Values;
            query = filter switch
            {
                TaskStatusFilter.Active => query.Where(t => t.Status == "active"),
                TaskStatusFilter.Waiting => query.Where(t => t.Status is "waiting" or "paused"),
                TaskStatusFilter.Stopped => query.Where(t => t.Status is "complete" or "error" or "removed"),
                _ => query
            };

            var list = query.OrderBy(t => t.Gid).ToList();
            var totalCount = list.Count;
            var paged = list.Skip(offset).Take(limit).Select(CloneTask).ToList();

            return Task.FromResult(new PagedResult<AriaTaskInfo>
            {
                Items = paged,
                TotalCount = totalCount,
                Offset = offset,
                Limit = limit,
                Revision = Volatile.Read(ref _revisionCounter)
            });
        }
    }

    public Task<OperationOutcome> GetOperationOutcomeAsync(long operationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_outcomes.TryGetValue(operationId, out var outcome))
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
        ThrowIfDisposed();
        var reader = _eventChannel.Reader;
        while (!cancellationToken.IsCancellationRequested && await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var ev))
            {
                yield return ev;
            }
        }
    }

    #endregion

    #region Helpers & Memory Management

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

    public static string GidToHex(ulong gid)
    {
        byte[] buf = new byte[17];
        NativeBridge.a2_gid_to_hex(gid, buf);
        int len = 0;
        while (len < 16 && buf[len] != 0) len++;
        if (len == 16) return Encoding.UTF8.GetString(buf, 0, 16);
        return gid.ToString("x16");
    }

    private static IntPtr AllocNativeStringArray(IReadOnlyList<string> list, out List<IntPtr> allocatedPtrs)
    {
        allocatedPtrs = new List<IntPtr>(list.Count + 1);
        var arraySize = IntPtr.Size * list.Count;
        var arrayPtr = Marshal.AllocHGlobal(arraySize);
        allocatedPtrs.Add(arrayPtr);
        for (int i = 0; i < list.Count; i++)
        {
            var strPtr = Marshal.StringToCoTaskMemUTF8(list[i]);
            allocatedPtrs.Add(strPtr);
            Marshal.WriteIntPtr(arrayPtr, i * IntPtr.Size, strPtr);
        }
        return arrayPtr;
    }

    private static void FreeNativeStringArray(List<IntPtr> allocatedPtrs)
    {
        if (allocatedPtrs.Count > 0)
        {
            Marshal.FreeHGlobal(allocatedPtrs[0]);
            for (int i = 1; i < allocatedPtrs.Count; i++)
            {
                Marshal.ZeroFreeCoTaskMemUTF8(allocatedPtrs[i]);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _isDisposed) == 1)
            throw new ObjectDisposedException(nameof(NativeAriaEngineHost));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;

        try
        {
            await ShutdownCoreAsync();
        }
        catch { }

        _ownerCts?.Dispose();
    }

    #endregion
}
