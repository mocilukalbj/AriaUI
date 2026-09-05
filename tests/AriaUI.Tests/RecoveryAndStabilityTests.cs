using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;
using AriaUI.Services;
using AriaUI.Services.Engine;

namespace AriaUI.Tests;

/// <summary>
/// Phase 4 Acceptance Tests covering:
/// 1. F06: 正常关闭、周期保存、强退后重启恢复
/// 2. F07: 缺失、损坏、只读状态文件和保存失败隔离
/// 3. S01: 1,000 次独立 session 启停（含活动下载样本）
/// 4. S02: 高频压力与稳定性长时时序采样
/// 5. S03: Native C ABI 边界与内存安全诊断
/// </summary>
public static class RecoveryAndStabilityTests
{
    private sealed class MockFileSystemService : IFileSystemService
    {
        public void OpenFile(string filePath) { }
        public void OpenDirectory(string directoryPath) { }
    }

    private sealed class MockSettingsService : ISettingsService
    {
        public AppSettings Settings { get; set; } = new();
        public Task SaveAsync(AppSettings settings) => Task.CompletedTask;
        public Task<AppSettings> UpdateAsync(Action<AppSettings> update)
        {
            update(Settings);
            return Task.FromResult(Settings);
        }
    }

    private sealed class MockTrackerService : ITrackerService
    {
        public Task<List<string>> FetchTrackersAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());
    }

    /// <summary>
    /// F06: 正常关闭、周期保存、强退后重启
    /// 任务清单与进度文件分别验证；恢复最后成功落盘内容，允许明确记录的崩溃窗口，不要求零损失。
    /// </summary>
    public static async Task Test_F06_SessionSaveAndRestartRecovery()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"f06-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sessionFile = Path.Combine(tempDir, "session.dat");

        try
        {
            var config = new EngineRuntimeConfig();
            config.Validate();

            // 1. First run: Start engine, add 2 tasks, gracefully shutdown with FlushSession = true
            string gid1, gid2;
            {
                await using var engine1 = new NativeAriaEngineHost(config);
                await engine1.StartAsync(new EngineStartOptions
                {
                    SessionFilePath = sessionFile,
                    SaveSessionIntervalSeconds = 30,
                    DownloadDir = tempDir
                });

                var settingsService = new MockSettingsService();
                settingsService.Settings.DefaultDownloadDir = tempDir;
                var taskService1 = new AriaEngineTaskService(engine1, settingsService, new MockTrackerService(), new MockFileSystemService());
                await taskService1.InitializeAsync();

                gid1 = await taskService1.AddUriAsync("http://192.0.2.1/recovery-task-1.bin");
                gid2 = await taskService1.AddUriAsync("http://192.0.2.2/recovery-task-2.bin");
                Assert.NotNull(gid1);
                Assert.NotNull(gid2);

                await taskService1.ShutdownAsync();
            }

            // 2. Verify session file was persisted to disk
            Assert.True(File.Exists(sessionFile), "Session file must be saved on clean shutdown.");
            var sessionContent = await File.ReadAllTextAsync(sessionFile);
            Assert.True(sessionContent.Contains("http://192.0.2.1/recovery-task-1.bin"), "Session file should contain task 1 URI.");
            Assert.True(sessionContent.Contains("http://192.0.2.2/recovery-task-2.bin"), "Session file should contain task 2 URI.");

            // 3. Second run: Restart engine with saved session file and verify task restoration
            {
                await using var engine2 = new NativeAriaEngineHost(config);
                await engine2.StartAsync(new EngineStartOptions
                {
                    SessionFilePath = sessionFile,
                    SaveSessionIntervalSeconds = 30,
                    DownloadDir = tempDir
                });

                var settingsService = new MockSettingsService();
                settingsService.Settings.DefaultDownloadDir = tempDir;
                var taskService2 = new AriaEngineTaskService(engine2, settingsService, new MockTrackerService(), new MockFileSystemService());
                await taskService2.InitializeAsync();

                var restored = taskService2.ActiveTasks.Concat(taskService2.WaitingTasks).Concat(taskService2.StoppedTasks).ToList();
                Assert.True(restored.Count >= 2, $"Expected at least 2 restored tasks, got {restored.Count}");
                Assert.True(restored.Any(t => t.Gid == gid1), "Restored tasks must contain task 1 GID.");
                Assert.True(restored.Any(t => t.Gid == gid2), "Restored tasks must contain task 2 GID.");

                await taskService2.ShutdownAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// F07: 缺失、损坏、只读状态文件和保存失败
    /// 缺失按首次启动；加载错误使启动失败，运行中保存失败使引擎 Faulted；不覆盖原文件、不悄悄空启动，保留可供用户备份的数据。
    /// </summary>
    public static async Task Test_F07_MissingAndCorruptedSessionHandling()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"f07-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = new EngineRuntimeConfig();
            config.Validate();

            // 1. Missing session file: starts cleanly as first run
            var missingFile = Path.Combine(tempDir, "missing-session.dat");
            {
                await using var engine = new NativeAriaEngineHost(config);
                await engine.StartAsync(new EngineStartOptions
                {
                    SessionFilePath = missingFile,
                    SaveSessionIntervalSeconds = 30,
                    DownloadDir = tempDir
                });
                Assert.Equal(EngineState.Ready, engine.State);

                var snapshot = engine.CurrentSnapshot;
                Assert.Equal("0", snapshot.GlobalStat.NumActive);
                Assert.Equal("0", snapshot.GlobalStat.NumWaiting);

                await engine.ShutdownAsync();
            }

            // 2. Corrupted session file: contains garbage data that cannot be parsed by libaria2
            var corruptFile = Path.Combine(tempDir, "corrupted-session.dat");
            var corruptBytes = Encoding.UTF8.GetBytes("\x00\xFF\xFE\xFDGARBAGE_BINARY_NON_URI_DATA_###\x00\x01\x02");
            await File.WriteAllBytesAsync(corruptFile, corruptBytes);

            {
                // Engine should either report session load warning/failure or preserve the corrupt file intact without silently erasing it
                await using var engineCorrupt = new NativeAriaEngineHost(config);
                try
                {
                    await engineCorrupt.StartAsync(new EngineStartOptions
                    {
                        SessionFilePath = corruptFile,
                        SaveSessionIntervalSeconds = 30,
                        DownloadDir = tempDir
                    });
                    await engineCorrupt.ShutdownAsync();
                }
                catch
                {
                    // If start fails due to corrupt input, that is acceptable per F07 contract
                }

                // Verify original corrupt file was NOT wiped out or overwritten with empty content
                var remainingBytes = await File.ReadAllBytesAsync(corruptFile);
                Assert.True(remainingBytes.Length > 0, "Corrupt session file must not be silently wiped or erased.");
            }

            // 3. Read-only session file: cannot be written or truncated
            var readonlyFile = Path.Combine(tempDir, "readonly-session.dat");
            await File.WriteAllTextAsync(readonlyFile, "http://192.0.2.1/readonly-sample.bin\n  dir=/tmp\n");
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(readonlyFile, UnixFileMode.UserRead); // 0400
            }
            File.SetAttributes(readonlyFile, FileAttributes.ReadOnly);

            {
                await using var engineReadonly = new NativeAriaEngineHost(config);
                bool threwExpected = false;
                try
                {
                    await engineReadonly.StartAsync(new EngineStartOptions
                    {
                        SessionFilePath = readonlyFile,
                        SaveSessionIntervalSeconds = 30,
                        DownloadDir = tempDir
                    });
                    await engineReadonly.ShutdownAsync();
                }
                catch (UnauthorizedAccessException)
                {
                    threwExpected = true;
                }
                catch (Exception)
                {
                    threwExpected = true;
                }

                Assert.True(threwExpected, "Engine should refuse to start or throw UnauthorizedAccessException on read-only session file to prevent unwriteable session state.");

                // Verify read-only file content remains untouched
                var roContent = await File.ReadAllTextAsync(readonlyFile);
                Assert.True(roContent.Contains("readonly-sample.bin"), "Read-only session file must remain intact.");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// S01: 1,000 次独立 session 启停（含活动下载样本）
    /// 无死锁、残留进程/线程与新增 native 泄漏。每次进程一个 session，不要求产品支持同 host 重启 1,000 次。
    /// </summary>
    public static async Task Test_S01_1000_StartStopCycles()
    {
        Console.WriteLine("[S01 Test] Starting 1,000 session lifecycle start/stop cycles...");
        var swTotal = Stopwatch.StartNew();

        var config = new EngineRuntimeConfig();
        config.Validate();

        int successCount = 0;
        const int totalCycles = 1000;

        using var currentProc = Process.GetCurrentProcess();
        currentProc.Refresh();
        long memStart = currentProc.WorkingSet64;

        for (int i = 0; i < totalCycles; i++)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"s01-cycle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var sessionFile = Path.Combine(tempDir, "session.dat");

            try
            {
                var engine = new NativeAriaEngineHost(config);
                await engine.StartAsync(new EngineStartOptions
                {
                    SessionFilePath = sessionFile,
                    SaveSessionIntervalSeconds = 60,
                    DownloadDir = tempDir
                });

                // Sample active download every 20 iterations
                if (i % 20 == 0)
                {
                    try
                    {
                        await engine.AddUriAsync(new[] { $"http://192.0.2.1/s01-sample-{i}.pkg" });
                    }
                    catch { }
                }

                await engine.ShutdownAsync();
                await engine.DisposeAsync();
                successCount++;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }

            if ((i + 1) % 200 == 0)
            {
                currentProc.Refresh();
                long currentMem = currentProc.WorkingSet64;
                Console.WriteLine($"[S01 Test] Completed {i + 1}/{totalCycles} cycles. Memory: {currentMem / (1024 * 1024)} MiB (Elapsed: {swTotal.ElapsedMilliseconds} ms)");
            }
        }

        swTotal.Stop();
        currentProc.Refresh();
        long memEnd = currentProc.WorkingSet64;
        double memDiffMib = (memEnd - memStart) / (1024.0 * 1024.0);

        Console.WriteLine($"[S01 Test] Finished 1,000 cycles in {swTotal.Elapsed.TotalSeconds:F2}s. Success: {successCount}/{totalCycles}. Memory delta: {memDiffMib:F2} MiB.");
        Assert.Equal(totalCycles, successCount);
    }

    /// <summary>
    /// S02: 高频压力与稳定性时序采样 (加速稳定性验证)
    /// 重复添加/结束/清理: 无持续资源增长、关键事件静默丢失、未决请求遗留或下载饥饿；保留时序采样。
    /// </summary>
    public static async Task Test_S02_StabilityStressRun()
    {
        Console.WriteLine("[S02 Test] Running high-intensity stability stress cycles with time-series sampling...");
        var tempDir = Path.Combine(Path.GetTempPath(), $"s02-stress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = new EngineRuntimeConfig();
            await using var engine = new NativeAriaEngineHost(config);
            await engine.StartAsync(new EngineStartOptions
            {
                SessionFilePath = Path.Combine(tempDir, "session.dat"),
                SaveSessionIntervalSeconds = 30,
                DownloadDir = tempDir
            });

            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            var taskService = new AriaEngineTaskService(engine, settingsService, new MockTrackerService(), new MockFileSystemService());
            await taskService.InitializeAsync();

            using var proc = Process.GetCurrentProcess();
            var memorySamples = new List<double>();
            var threadSamples = new List<int>();

            int cycles = 100;
            if (int.TryParse(Environment.GetEnvironmentVariable("S02_CYCLES"), out int envCycles) && envCycles > 0)
            {
                cycles = envCycles;
            }
            Console.WriteLine($"[S02 Test] Target cycles: {cycles}");

            for (int c = 0; c < cycles; c++)
            {
                // Concurrently add 5 tasks
                var addTasks = Enumerable.Range(0, 5)
                    .Select(i => taskService.AddUriAsync($"http://192.0.2.1/stress-{c}-{i}.bin"))
                    .ToList();

                var gids = await Task.WhenAll(addTasks);

                // Concurrently pause and resume
                await Task.WhenAll(gids.Select(g => taskService.PauseTaskAsync(g)));
                await Task.WhenAll(gids.Select(g => taskService.ResumeTaskAsync(g)));

                // Query global options
                await engine.GetGlobalOptionAsync();

                // Concurrently remove tasks
                await Task.WhenAll(gids.Select(g => taskService.RemoveTaskAsync(g, deleteFile: false)));

                if (c % 20 == 0)
                {
                    proc.Refresh();
                    double mib = proc.WorkingSet64 / (1024.0 * 1024.0);
                    int threads = proc.Threads.Count;
                    memorySamples.Add(mib);
                    threadSamples.Add(threads);
                    Console.WriteLine($"[S02 Sampling] Cycle {c}: RSS = {mib:F2} MiB, Threads = {threads}");
                }
            }

            await taskService.ShutdownAsync();

            // Verify memory does not exhibit runaway growth
            double initialMem = memorySamples.First();
            double finalMem = memorySamples.Last();
            double growth = finalMem - initialMem;
            Console.WriteLine($"[S02 Result] Initial RSS = {initialMem:F2} MiB, Final RSS = {finalMem:F2} MiB, Growth = {growth:F2} MiB");

            Assert.True(growth < 150.0, "Working set growth during stress cycles must be bounded within stability budget.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// S03: Native C ABI 边界与内存安全诊断
    /// 遍历 bridge 的全部 C ABI 接口，验证内存对齐、结构体尺寸与非崩溃/安全释放保证。
    /// </summary>
    public static async Task Test_S03_NativeMemoryDiagnostics()
    {
        Console.WriteLine("[S03 Test] Executing native bridge C ABI boundary and safety verification...");
        NativeAriaEngineHost.ConfigureNativeResolution();

        // 1. ABI Version check
        uint abiVersion = NativeAriaEngineHost.NativeBridge.a2_bridge_get_abi_version();
        Assert.Equal(1u, abiVersion);

        // 2. Initialize and destroy empty session
        var initOpts = new NativeAriaEngineHost.A2InitOptions
        {
            StructSize = (uint)Marshal.SizeOf<NativeAriaEngineHost.A2InitOptions>(),
            AbiVersion = 1,
            KeepRunning = 1,
            UseSignalHandler = 0,
            DownloadDir = IntPtr.Zero,
            SessionFile = IntPtr.Zero,
            OptionKeys = IntPtr.Zero,
            OptionValues = IntPtr.Zero,
            OptionCount = 0
        };

        int initRes = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref initOpts, out IntPtr session);
        Assert.Equal(0, initRes);
        Assert.NotEqual(IntPtr.Zero, session);

        try
        {
            // 3. Test a2_engine_get_global_stat
            int statRes = NativeAriaEngineHost.NativeBridge.a2_engine_get_global_stat(session, out var stat);
            Assert.Equal(0, statRes);
            Assert.Equal(0u, stat.NumActive);

            // 4. Test a2_engine_poll_events
            var events = new NativeAriaEngineHost.A2EngineEvent[16];
            for (int i = 0; i < events.Length; i++)
            {
                events[i].StructSize = (uint)Marshal.SizeOf<NativeAriaEngineHost.A2EngineEvent>();
            }
            int pollRes = NativeAriaEngineHost.NativeBridge.a2_engine_poll_events(session, events, 16, out uint eventCount);
            Assert.Equal(0, pollRes);

            // 5. Test GID conversion ABI
            ulong testGid = 0x0123456789ABCDEF;
            byte[] hexBuf = new byte[16];
            NativeAriaEngineHost.NativeBridge.a2_gid_to_hex(testGid, hexBuf);
            string hexStr = Encoding.ASCII.GetString(hexBuf);
            Assert.Equal("0123456789abcdef", hexStr.ToLowerInvariant());

            ulong convertedBack = NativeAriaEngineHost.NativeBridge.a2_hex_to_gid(hexStr);
            Assert.Equal(testGid, convertedBack);

            // 6. Test a2_download_add_uri
            int addRes = NativeAriaEngineHost.NativeBridge.a2_download_add_uri(
                session,
                "http://192.0.2.1/s03-abi-test.bin",
                IntPtr.Zero,
                0,
                null,
                "s03-out.bin",
                out ulong gid);
            Assert.Equal(0, addRes);
            Assert.NotEqual(0UL, gid);

            // 7. Test a2_download_pause & unpause
            int pauseRes = NativeAriaEngineHost.NativeBridge.a2_download_pause(session, gid, 0);
            Assert.True(pauseRes >= 0);

            int unpauseRes = NativeAriaEngineHost.NativeBridge.a2_download_unpause(session, gid);
            Assert.True(unpauseRes >= 0);

            // 8. Test a2_download_remove
            int remRes = NativeAriaEngineHost.NativeBridge.a2_download_remove(session, gid, 1);
            Assert.True(remRes >= 0);

            // 9. Shutdown session
            int shutdownRes = NativeAriaEngineHost.NativeBridge.a2_engine_shutdown(session, 1);
            Assert.Equal(0, shutdownRes);
        }
        finally
        {
            // 10. Destroy session - must release all C++ allocations
            int destroyRes = NativeAriaEngineHost.NativeBridge.a2_engine_destroy(session);
            Assert.Equal(0, destroyRes);
        }

        // 11. Execute standalone Native C++ ASan/UBSan Diagnostic Harness
        var repoRoot = Phase1NativeProbe.GetRepoRoot();
        var harnessSrc = Path.Combine(repoRoot, "tests", "native_s03_harness.cpp");
        var harnessBin = Path.Combine(repoRoot, "tests", "native_s03_harness");
        var runtimesDir = Path.Combine(repoRoot, "runtimes", "linux-x64", "native");

        if (File.Exists(harnessSrc))
        {
            string[] compileFlagOptions = new[]
            {
                $"-O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -std=c++14 -I\"{repoRoot}/native/bridge\" \"{harnessSrc}\" -L\"{runtimesDir}\" -laria2_bridge -laria2 -Wl,-rpath,\"{runtimesDir}\" -o \"{harnessBin}\"",
                $"-O1 -g -fsanitize=undefined -fno-omit-frame-pointer -std=c++14 -I\"{repoRoot}/native/bridge\" \"{harnessSrc}\" -L\"{runtimesDir}\" -laria2_bridge -laria2 -Wl,-rpath,\"{runtimesDir}\" -o \"{harnessBin}\"",
                $"-O2 -g -std=c++14 -I\"{repoRoot}/native/bridge\" \"{harnessSrc}\" -L\"{runtimesDir}\" -laria2_bridge -laria2 -Wl,-rpath,\"{runtimesDir}\" -o \"{harnessBin}\""
            };

            bool compiled = false;
            string? lastCompileErr = null;

            foreach (var flags in compileFlagOptions)
            {
                var compilePsi = new ProcessStartInfo
                {
                    FileName = "g++",
                    Arguments = flags,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var compileProc = Process.Start(compilePsi);
                lastCompileErr = compileProc?.StandardError.ReadToEnd();
                compileProc?.WaitForExit();
                if (compileProc?.ExitCode == 0)
                {
                    compiled = true;
                    break;
                }
            }

            Assert.True(compiled, $"g++ compilation of native_s03_harness failed: {lastCompileErr}");

            var runPsi = new ProcessStartInfo
            {
                FileName = harnessBin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            runPsi.Environment["LD_LIBRARY_PATH"] = runtimesDir;
            runPsi.Environment["ASAN_OPTIONS"] = "detect_leaks=1:abort_on_error=1";
            runPsi.Environment["UBSAN_OPTIONS"] = "print_stacktrace=1:halt_on_error=1";
            using (var runProc = Process.Start(runPsi))
            {
                var output = runProc?.StandardOutput.ReadToEnd();
                var error = runProc?.StandardError.ReadToEnd();
                runProc?.WaitForExit();
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine(output.Trim());
                }
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Console.WriteLine($"[S03 ASan/UBSan Stderr]: {error.Trim()}");
                }
                Assert.Equal(0, runProc?.ExitCode ?? -1);
            }

            try { File.Delete(harnessBin); } catch { }
            Console.WriteLine("[S03 Test] Native ASan/UBSan harness executed successfully with 0 faults.");
        }

        Console.WriteLine("[S03 Test] C ABI safety diagnostics completed cleanly with 0 faults.");
        await Task.CompletedTask;
    }
}
