using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AriaUI.Services.Engine;

namespace AriaUI.Tests;

public static class Program
{
    private sealed record TestCase(string Id, string Description, Func<Task> Action);

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine(" AriaUI Test Runner (LIBARIA2_TEST_PLAN Phase 2) ");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Timestamp: {DateTime.UtcNow:O}");
        Console.WriteLine($"Platform:  {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"Runtime:   .NET {Environment.Version}");
        Console.WriteLine();

        // Ensure native bridge and library are built and resolvable
        await Phase1NativeProbe.BuildNativeLibrariesAsync();
        NativeAriaEngineHost.ConfigureNativeResolution();

        var testCases = new List<TestCase>
        {
            // --- Fake Engine Contract Tests (C01-C12) ---
            new("Fake/C01", "Fake: 空任务常驻 Ready 与后续任务接纳", () => ContractTests.Test_C01_EmptyStartAndContinuousTaskAdd(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C02", "Fake: 生命周期状态契约与重复调用验证", () => ContractTests.Test_C02_LifecycleAndStateValidation(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C03", "Fake: Starting 中关闭及初始化失败隔离", () => ContractTests.Test_C03_ShutdownDuringStartingAndInitFailure(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C04", "Fake: 满队列/已接纳命令在关闭期限内处理与超时隔离", () => ContractTests.Test_C04_ShutdownWithFullQueueAndDeadline(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C06", "Fake: 命令级错误隔离与 Fatal 终态注入", () => ContractTests.Test_C06_CommandErrorsAndFatalFaultInjection(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C07", "Fake: 取消竞态判定与调用方超时隔离", () => ContractTests.Test_C07_CancellationRaceAndCallerTimeout(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C08", "Fake: 有界队列背压与 Continuation 隔离", () => ContractTests.Test_C08_BoundedQueueAndContinuationIsolation(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C09", "Fake: 突破100条限制的分页查询与修订号一致性 (F05)", () => ContractTests.Test_C09_PagedQueriesAndRevisionConsistency(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C10", "Fake: 全局与任务实际选项读取、继承与更新 (F04)", () => ContractTests.Test_C10_OptionsInspectionAndRealValues(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C11", "Fake: §8.3: 多值/重复 Header 集合支持", () => ContractTests.Test_C11_MultiValueRepeatedHeaders(cfg => new FakeAriaEngine(cfg))),
            new("Fake/C12", "Fake: C07/G08-G09: 操作结果查询与调用方超时解耦", () => ContractTests.Test_C12_OperationOutcomeTrackingAndTimeoutDecoupling(cfg => new FakeAriaEngine(cfg))),

            // --- Native Engine Host Dual-Run Contract Tests (C01-C12 against real libaria2.so) ---
            new("Native/C01", "Native: 空任务常驻 Ready 与后续任务接纳 (真 bridge)", () => ContractTests.Test_C01_EmptyStartAndContinuousTaskAdd(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C02", "Native: 生命周期状态契约与重复调用验证 (真 bridge)", () => ContractTests.Test_C02_LifecycleAndStateValidation(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C03", "Native: Starting 中关闭及初始化失败隔离 (真 bridge)", () => ContractTests.Test_C03_ShutdownDuringStartingAndInitFailure(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C04", "Native: 满队列/已接纳命令在关闭期限内处理与超时隔离 (真 bridge)", () => ContractTests.Test_C04_ShutdownWithFullQueueAndDeadline(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C06", "Native: 命令级错误隔离与 Fatal 终态注入 (真 bridge)", () => ContractTests.Test_C06_CommandErrorsAndFatalFaultInjection(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C07", "Native: 取消竞态判定与调用方超时隔离 (真 bridge)", () => ContractTests.Test_C07_CancellationRaceAndCallerTimeout(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C08", "Native: 有界队列背压与 Continuation 隔离 (真 bridge)", () => ContractTests.Test_C08_BoundedQueueAndContinuationIsolation(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C09", "Native: 突破100条限制的分页查询与修订号一致性 (真 bridge)", () => ContractTests.Test_C09_PagedQueriesAndRevisionConsistency(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C10", "Native: 全局与任务实际选项读取、继承与更新 (真 bridge)", () => ContractTests.Test_C10_OptionsInspectionAndRealValues(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C11", "Native: §8.3: 多值/重复 Header 集合支持 (真 bridge)", () => ContractTests.Test_C11_MultiValueRepeatedHeaders(cfg => new NativeAriaEngineHost(cfg))),
            new("Native/C12", "Native: C07/G08-G09: 操作结果查询与调用方超时解耦 (真 bridge)", () => ContractTests.Test_C12_OperationOutcomeTrackingAndTimeoutDecoupling(cfg => new NativeAriaEngineHost(cfg))),

            // --- ABI Tests (A01 - A05) ---
            new("A01", "LIBARIA2_TEST_PLAN §3.1: ABI 版本校验、尺寸检查与跨线程隔离", AbiTests.Test_A01_AbiVersionAndWrongThreadRejection),
            new("A02", "LIBARIA2_TEST_PLAN §3.1: UTF-8、中文路径与边界长度校验", AbiTests.Test_A02_Utf8AndBoundaryValidation),
            new("A03", "LIBARIA2_TEST_PLAN §3.1: 缓冲区容量检查与两阶段重试 (BUFFER_TOO_SMALL)", AbiTests.Test_A03_BufferTooSmallAndResizeProtocol),
            new("A04", "LIBARIA2_TEST_PLAN §3.1: 事件缓冲读取与不出队保证", AbiTests.Test_A04_EventPollingProtocol),
            new("A05", "LIBARIA2_TEST_PLAN §3.1: Handle 退出路径与异常不跨越 ABI", AbiTests.Test_A05_HandleReleaseAndExceptionSafety),

            // --- Historical Regressions & Protection Tests (F03, F04, F07, F09, U01) ---
            new("F09", "REVIEW #47/#53: Tracker SSRF 校验与正文上限保护", HistoricalRegressionTests.Test_F09_TrackerSsrfAndResourceProtection),
            new("F04", "REVIEW #50/#52/#59: 设置多级校验、空目录规范化与配置隔离", () => { HistoricalRegressionTests.Test_F04_F07_SettingsValidationAndNormalization(); return Task.CompletedTask; }),
            new("F03", "REVIEW #54: 任务删除前本地文件路径与安全目录校验", () => { HistoricalRegressionTests.Test_F03_TaskRemovalPathProtection(); return Task.CompletedTask; }),
            new("U01", "REVIEW #58: TaskListView 筛选按钮派生布尔属性与属性变更通知", () => { HistoricalRegressionTests.Test_U01_TaskListFilterSelectionState(); return Task.CompletedTask; }),

            // --- BOUNDARIES.md Section 8: RPC Baseline Regressions (B01, B02, B03, B04, B05) ---
            new("B01", "BOUNDARIES §8.1: ProcessIncomingMessage 脏帧注入与接收循环存活", () => { RpcBaselineRegressionTests.Test_RPC_DirtyFrameResilience(); return Task.CompletedTask; }),
            new("B02", "BOUNDARIES §8.2: PollLoopAsync 单轮故障隔离与轮询循环存活", RpcBaselineRegressionTests.Test_RPC_PollingTickFailureResilience),
            new("B03", "BOUNDARIES §8.3: 瞬断后 3s 内独立重连恢复快照 (不依赖轮询存活)", RpcBaselineRegressionTests.Test_RPC_TransientDisconnectReconnectWithin3sAndRestoreSnapshot),
            new("B04", "BOUNDARIES §8.3: 初始连接失败自动重连与快照恢复", RpcBaselineRegressionTests.Test_RPC_InitialConnectFailureAutoReconnectsWhenServerOnline),
            new("B05", "BOUNDARIES §8.3 & §7: 任务服务关闭时确定性取消并等待重连任务", RpcBaselineRegressionTests.Test_RPC_ShutdownAwaitsReconnectTask),

            // --- LIBARIA2_TEST_PLAN.md §4: M01 Real RPC Performance & Resource Baseline ---
            new("M01", "LIBARIA2_TEST_PLAN §4: 真实 RPC 启动、命令延迟(3轮x1000次)与资源基线测量", async () =>
            {
                var report = await M01RpcBaselineBenchmark.RunBenchmarkAsync(startupIterations: 30, commandIterations: 1000);
                Assert.True(report.EmptyStartup.P95Ms < 500, $"Empty startup P95 should meet 500ms experimental target, got {report.EmptyStartup.P95Ms}ms");
                Assert.True(report.GlobalStatBenchmark.MedianP95Ms > 0, "Median P95 command latency must be recorded");
            }),

            // --- LIBARIA2_TEST_PLAN.md §5: Phase 1 Native Probe (P01) ---
            new("P01", "阶段 1 原生探针：libaria2 + C ABI bridge 构建与 keepRunning/RUN_ONCE/CPU 实测", async () =>
            {
                var report = await Phase1NativeProbe.RunProbeAsync();
                Assert.True(report.KeepRunningEmptySessionReturnsOne, "keepRunning=true must return 1 on empty session");
                Assert.True(report.RunOnceTimeoutMs > 500, "RUN_ONCE should wait around 1 second in idle epoll");
                Assert.True(report.IdleCpuPercent < 5.0, "Idle CPU should be near zero during RUN_ONCE waiting");
                Assert.True(report.WrongThreadRejected, "Cross-thread call must be rejected with A2_STATUS_WRONG_THREAD");
            }),

            // --- Phase 3 Step 1: Application Wiring & Lifecycle ---
            new("Step1/Wiring", "Step 1: AriaEngineTaskService 完整生命周期 (添加/暂停/恢复/删除/设置/关闭)", ApplicationWiringAndGatewayTests.Test_Step1_ApplicationWiring_CompleteLifecycle),
            new("Step1/P03", "Step 1 / P03: Native 模式无 aria2c 子进程且无控制端口", ApplicationWiringAndGatewayTests.Test_Step1_NoAria2cSubprocessAndNoControlPort),
            new("Step1/PerfComp", "Step 1: 同负载同操作(AddUriAsync)端到端真实结果延迟对比", ApplicationWiringAndGatewayTests.Test_Step1_PerformanceComparison_SubmitToRealResult),

            // --- Phase 3 Step 2: Gateway & Protocol Compliance (G01 - G12) ---
            new("G01", "G01: Manifest/扩展白名单与任意RPC/shell拦截", ApplicationWiringAndGatewayTests.Test_G01_ManifestAndAllowedActions),
            new("G02", "G02: 32位长度前缀分段读取与坏长度/EOF处理", ApplicationWiringAndGatewayTests.Test_G02_FrameProtocol_ChunkedAndMalformed),
            new("G03", "G03: 64KiB边界校验、CRLF/NUL注入拦截与路径穿越防护", ApplicationWiringAndGatewayTests.Test_G03_64KiBBoundary_HeaderInjection_PathTraversal),
            new("G04", "G04: 容量门禁：最多8连接/每扩展10req/s限流/单未决请求", ApplicationWiringAndGatewayTests.Test_G04_CapacityLimits_MaxConnections_RateLimiting),
            new("G05", "G05: SO_PEERCRED UID隔离与0700/0600权限", ApplicationWiringAndGatewayTests.Test_G05_UidPeerCredentialsAndPermissions),
            new("G06_G07", "G06/G07: App互斥争锁、冷启动与陈旧Socket清理", ApplicationWiringAndGatewayTests.Test_G06_G07_AppLocking_StaleSocketHandling),
            new("G08_G09", "G08/G09: 请求去重、载荷冲突检测与1024条缓存容量管理", ApplicationWiringAndGatewayTests.Test_G08_G09_Deduplication_ConflictDetection_CacheCapacity),
            new("G10_G11", "G10/G11: GET/magnet支持与不支持scheme(POST/blob/data)拒绝", ApplicationWiringAndGatewayTests.Test_G10_G11_GetAndMagnet_UnsupportedSchemes),
            new("G12", "G12: 宿主断开生命周期退出与App任务执行连续性", ApplicationWiringAndGatewayTests.Test_G12_HostLifecycle_DownloadContinuity),

            // --- Phase 3 Step 3 & End-to-End: Browser Takeover Pipeline ---
            new("Host/Startup", "Host: 薄宿主标准启动与协议头解析", ApplicationWiringAndGatewayTests.Test_Host_NativeMessagingStartupAndColdStartDiscovery),
            new("E2E/Takeover", "E2E: 浏览器提交 -> App接收 -> Native返回真实GID -> UI显示任务 -> 确认接管闭环", ApplicationWiringAndGatewayTests.Test_EndToEnd_BrowserTakeoverFlow),
        };

        int passed = 0;
        int failed = 0;
        var swTotal = Stopwatch.StartNew();

        foreach (var test in testCases)
        {
            var sw = Stopwatch.StartNew();
            Console.Write($"[{test.Id}] {test.Description} ... ");
            try
            {
                await test.Action();
                sw.Stop();
                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS ({sw.ElapsedMilliseconds} ms)");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL ({sw.ElapsedMilliseconds} ms)");
                Console.ResetColor();
                Console.WriteLine($"  Error: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                Console.WriteLine($"  Stack: {ex.StackTrace}");
            }
        }

        swTotal.Stop();
        Console.WriteLine();
        Console.WriteLine("-------------------------------------------------");
        Console.WriteLine($"Total: {testCases.Count} | Passed: {passed} | Failed: {failed} | Time: {swTotal.ElapsedMilliseconds} ms");
        Console.WriteLine("-------------------------------------------------");

        return failed == 0 ? 0 : 1;
    }
}
