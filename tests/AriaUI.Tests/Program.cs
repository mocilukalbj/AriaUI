using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AriaUI.Tests;

public static class Program
{
    private sealed record TestCase(string Id, string Description, Func<Task> Action);

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine(" AriaUI Test Runner (LIBARIA2_TEST_PLAN Phase 0) ");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Timestamp: {DateTime.UtcNow:O}");
        Console.WriteLine($"Platform:  {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"Runtime:   .NET {Environment.Version}");
        Console.WriteLine();

        var testCases = new List<TestCase>
        {
            // Contract Tests (C01, C02, C03, C04, C06, C07, C08, C09, C10, C11, C12)
            new("C01", "空任务常驻 Ready 与后续任务接纳", ContractTests.Test_C01_EmptyStartAndContinuousTaskAdd),
            new("C02", "生命周期状态契约与重复调用验证", ContractTests.Test_C02_LifecycleAndStateValidation),
            new("C03", "Starting 中关闭及初始化失败隔离", ContractTests.Test_C03_ShutdownDuringStartingAndInitFailure),
            new("C04", "满队列/已接纳命令在关闭期限内处理与超时隔离", ContractTests.Test_C04_ShutdownWithFullQueueAndDeadline),
            new("C06", "命令级错误隔离与 Fatal 终态注入", ContractTests.Test_C06_CommandErrorsAndFatalFaultInjection),
            new("C07", "取消竞态判定与调用方超时隔离", ContractTests.Test_C07_CancellationRaceAndCallerTimeout),
            new("C08", "有界队列背压与 Continuation 隔离", ContractTests.Test_C08_BoundedQueueAndContinuationIsolation),
            new("C09", "F05: 突破100条限制的分页查询与修订号一致性", ContractTests.Test_C09_PagedQueriesAndRevisionConsistency),
            new("C10", "F04: 全局与任务实际选项读取、继承与更新", ContractTests.Test_C10_OptionsInspectionAndRealValues),
            new("C11", "§8.3: 多值/重复 Header 集合支持", ContractTests.Test_C11_MultiValueRepeatedHeaders),
            new("C12", "C07/G08-G09: 操作结果查询与调用方超时解耦", ContractTests.Test_C12_OperationOutcomeTrackingAndTimeoutDecoupling),

            // Historical Regressions & Protection Tests (F03, F04, F07, F09, U01)
            new("F09", "REVIEW #47/#53: Tracker SSRF 校验与正文上限保护", HistoricalRegressionTests.Test_F09_TrackerSsrfAndResourceProtection),
            new("F04", "REVIEW #50/#52/#59: 设置多级校验、空目录规范化与配置隔离", () => { HistoricalRegressionTests.Test_F04_F07_SettingsValidationAndNormalization(); return Task.CompletedTask; }),
            new("F03", "REVIEW #54: 任务删除前本地文件路径与安全目录校验", () => { HistoricalRegressionTests.Test_F03_TaskRemovalPathProtection(); return Task.CompletedTask; }),
            new("U01", "REVIEW #58: TaskListView 筛选按钮派生布尔属性与属性变更通知", () => { HistoricalRegressionTests.Test_U01_TaskListFilterSelectionState(); return Task.CompletedTask; }),

            // BOUNDARIES.md Section 8: RPC Baseline Regressions (B01, B02, B03, B04, B05)
            new("B01", "BOUNDARIES §8.1: ProcessIncomingMessage 脏帧注入与接收循环存活", () => { RpcBaselineRegressionTests.Test_RPC_DirtyFrameResilience(); return Task.CompletedTask; }),
            new("B02", "BOUNDARIES §8.2: PollLoopAsync 单轮故障隔离与轮询循环存活", RpcBaselineRegressionTests.Test_RPC_PollingTickFailureResilience),
            new("B03", "BOUNDARIES §8.3: 瞬断后 3s 内独立重连恢复快照 (不依赖轮询存活)", RpcBaselineRegressionTests.Test_RPC_TransientDisconnectReconnectWithin3sAndRestoreSnapshot),
            new("B04", "BOUNDARIES §8.3: 初始连接失败自动重连与快照恢复", RpcBaselineRegressionTests.Test_RPC_InitialConnectFailureAutoReconnectsWhenServerOnline),
            new("B05", "BOUNDARIES §8.3 & §7: 任务服务关闭时确定性取消并等待重连任务", RpcBaselineRegressionTests.Test_RPC_ShutdownAwaitsReconnectTask),

            // LIBARIA2_TEST_PLAN.md §4: M01 Real RPC Performance & Resource Baseline
            new("M01", "LIBARIA2_TEST_PLAN §4: 真实 RPC 启动、命令延迟(3轮x1000次)与资源基线测量", async () =>
            {
                var report = await M01RpcBaselineBenchmark.RunBenchmarkAsync(startupIterations: 30, commandIterations: 1000);
                Assert.True(report.EmptyStartup.P95Ms < 500, $"Empty startup P95 should meet 500ms experimental target, got {report.EmptyStartup.P95Ms}ms");
                Assert.True(report.GlobalStatBenchmark.MedianP95Ms > 0, "Median P95 command latency must be recorded");
            }),

            // LIBARIA2_TEST_PLAN.md §5: Phase 1 Native Probe (P01)
            new("P01", "阶段 1 原生探针：libaria2 + C ABI bridge 构建与 keepRunning/RUN_ONCE/CPU 实测", async () =>
            {
                var report = await Phase1NativeProbe.RunProbeAsync();
                Assert.True(report.KeepRunningEmptySessionReturnsOne, "keepRunning=true must return 1 on empty session");
                Assert.True(report.RunOnceTimeoutMs > 500, "RUN_ONCE should wait around 1 second in idle epoll");
                Assert.True(report.IdleCpuPercent < 5.0, "Idle CPU should be near zero during RUN_ONCE waiting");
                Assert.True(report.WrongThreadRejected, "Cross-thread call must be rejected with A2_STATUS_WRONG_THREAD");
            }),
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
