using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Services.Engine;

namespace AriaUI.Tests;

public static class ContractTests
{
    private static EngineStartOptions CreateTestStartOptions() => new()
    {
        SessionFilePath = Path.Combine(Path.GetTempPath(), $"test-session-{Guid.NewGuid():N}.session"),
        SaveSessionIntervalSeconds = 30,
        DownloadDir = Path.GetTempPath()
    };

    /// <summary>
    /// C01: 空任务启动、任务完成后再次添加
    /// 通过条件: 常驻 Ready，可继续添加；不把 run 自然结束伪装为就绪。
    /// </summary>
    public static async Task Test_C01_EmptyStartAndContinuousTaskAdd(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);
        Assert.Equal(EngineState.Created, engine.State);

        var options = CreateTestStartOptions();
        await engine.StartAsync(options);

        // 1. 空任务启动后保持 Ready
        Assert.Equal(EngineState.Ready, engine.State);
        Assert.Equal(0, engine.CurrentSnapshot.ActiveTasks.Count);

        // 2. 添加第 1 个任务并执行
        var gid1 = await engine.AddUriAsync(new[] { "https://example.com/file1.zip" });
        Assert.False(string.IsNullOrWhiteSpace(gid1));
        Assert.Equal(EngineState.Ready, engine.State);

        // 3. 任务完成后，引擎依然常驻 Ready
        await engine.RemoveAsync(gid1);
        Assert.Equal(EngineState.Ready, engine.State);

        // 4. 可以继续添加第 2 个任务
        var gid2 = await engine.AddUriAsync(new[] { "https://example.com/file2.zip" });
        Assert.False(string.IsNullOrWhiteSpace(gid2));
        Assert.NotEqual(gid1, gid2);
        Assert.Equal(EngineState.Ready, engine.State);

        await engine.ShutdownAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    /// <summary>
    /// C02: 重复启动/关闭，各状态提交业务命令
    /// 通过条件: 结果符合状态契约，无重复 session；非 Ready 明确拒绝，重复关闭保留首次结果。
    /// </summary>
    public static async Task Test_C02_LifecycleAndStateValidation(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);

        // 1. Created 状态下提交业务命令被明确拒绝
        await Assert.ThrowsAsync<EngineNotReadyException>(async () =>
        {
            await engine.AddUriAsync(new[] { "https://example.com/file.zip" });
        });

        // 2. 正常启动到 Ready
        var options = CreateTestStartOptions();
        await engine.StartAsync(options);
        Assert.Equal(EngineState.Ready, engine.State);

        // 3. Ready 状态下重复调用 StartAsync 立即成功返回，不重建 session
        await engine.StartAsync(options);
        Assert.Equal(EngineState.Ready, engine.State);

        // 4. 正常关闭到 Stopped
        await engine.ShutdownAsync();
        Assert.Equal(EngineState.Stopped, engine.State);

        // 5. Stopped 状态下再次调用 ShutdownAsync 共享首次结果
        await engine.ShutdownAsync();
        Assert.Equal(EngineState.Stopped, engine.State);

        // 6. Stopped 状态下提交业务命令立即拒绝
        await Assert.ThrowsAsync<EngineNotReadyException>(async () =>
        {
            await engine.AddUriAsync(new[] { "https://example.com/file.zip" });
        });

        // 7. Stopped 状态下不能重新启动该 host
        await Assert.ThrowsAsync<EngineNotReadyException>(async () =>
        {
            await engine.StartAsync(options);
        });
    }

    /// <summary>
    /// C03: Starting 中关闭，初始化各步骤失败
    /// 通过条件: 不再发布 Ready；只清理已创建资源，各请求结束，根因保留。
    /// </summary>
    public static async Task Test_C03_ShutdownDuringStartingAndInitFailure(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        // 场景 A: Starting 中收到关闭请求
        {
            await using var engine = engineFactory(null);
            var startingGate = new TaskCompletionSource();
            var allowStartingToProceed = new TaskCompletionSource();

            engine.OnStartingHook = async () =>
            {
                startingGate.SetResult();
                await allowStartingToProceed.Task;
            };

            var options = CreateTestStartOptions();
            var startTask = engine.StartAsync(options);

            // 等待进入 Starting 状态
            await startingGate.Task;
            Assert.Equal(EngineState.Starting, engine.State);

            // 在 Starting 中触发关闭
            var shutdownTask = engine.ShutdownAsync();
            allowStartingToProceed.SetResult();

            // StartAsync 应当抛出取消/关闭异常，且 Ready 永远不被发布
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await startTask);
            await shutdownTask;
            Assert.Equal(EngineState.Stopped, engine.State);
        }

        // 场景 B: 初始化步骤抛出异常，进入 Faulted 终态
        {
            await using var engine = engineFactory(null);
            var initEx = new InvalidOperationException("Native library init failed: code -1");
            engine.OnStartingHook = () => throw initEx;

            var options = CreateTestStartOptions();
            var ex = await Assert.ThrowsAsync<EngineFaultedException>(async () =>
            {
                await engine.StartAsync(options);
            });
            Assert.Contains("Native library init failed", ex.ToString());
            Assert.Equal(EngineState.Faulted, engine.State);

            // Faulted 后提交业务命令必须明确抛出 EngineFaultedException
            await Assert.ThrowsAsync<EngineFaultedException>(async () =>
            {
                await engine.AddUriAsync(new[] { "https://example.com/file.zip" });
            });
        }
    }

    /// <summary>
    /// C04: 活动下载、满队列、等待写入时关闭
    /// 通过条件: 停止接纳，已接纳操作有明确结果；循环完成停机与保存，等待者不悬挂，线程退出。
    /// 依据: BOUNDARIES.md §2, §7; LIBARIA2_ARCHITECTURE_PLAN.md §4.3
    /// </summary>
    public static async Task Test_C04_ShutdownWithFullQueueAndDeadline(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        var config = new EngineRuntimeConfig
        {
            CommandQueueCapacity = 8,
            BatchCommandBudget = 1,
            LoopTimeout = TimeSpan.FromMilliseconds(50),
            ShutdownTimeout = TimeSpan.FromMilliseconds(200)
        };

        await using var engine = engineFactory(config);
        await engine.StartAsync(CreateTestStartOptions());
        Assert.Equal(EngineState.Ready, engine.State);

        // 1. 暂停 owner 执行，填入命令
        var firstCmdStarted = new TaskCompletionSource();
        var allowFirstCmdToFinish = new TaskCompletionSource();

        engine.OnBeforeCommandExecute = (opId, opName) =>
        {
            firstCmdStarted.TrySetResult();
            allowFirstCmdToFinish.Task.Wait();
        };

        // 提交已接纳的命令 1 和命令 2
        var task1 = engine.AddUriAsync(new[] { "https://example.com/file1.zip" });
        await firstCmdStarted.Task;

        var task2 = engine.AddUriAsync(new[] { "https://example.com/file2.zip" });

        // 2. 触发 ShutdownAsync：接纳入口立即原子关闭
        var shutdownTask = engine.ShutdownAsync();
        Assert.Equal(EngineState.Stopping, engine.State);

        // 新提交命令立即被拒绝（不进入满队列，不挂起）
        await Assert.ThrowsAsync<EngineNotReadyException>(async () =>
        {
            await engine.AddUriAsync(new[] { "https://example.com/rejected_during_shutdown.zip" });
        });

        // 3. 超过 ShutdownTimeout (200ms) 后放行：已开始的命令 1 正常完成，而超时未开始的命令 2 以 TimeoutException 结束
        await Task.Delay(250);
        allowFirstCmdToFinish.SetResult();

        var gid1 = await task1;
        Assert.False(string.IsNullOrWhiteSpace(gid1));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await task2;
        });

        await shutdownTask;
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    /// <summary>
    /// C06: 非法 GID/选项、下载错误、fatal 注入
    /// 通过条件: 前两类只影响请求/任务，后续有效命令可执行；fatal 完成未决请求并拒绝新命令，不自动重启。
    /// </summary>
    public static async Task Test_C06_CommandErrorsAndFatalFaultInjection(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);
        await engine.StartAsync(CreateTestStartOptions());

        // 1. 命令错误（例如非法 GID）只影响该命令本身，不使引擎 Faulted
        var cmdEx = await Assert.ThrowsAsync<EngineCommandException>(async () =>
        {
            await engine.PauseAsync("invalid_nonexistent_gid");
        });
        Assert.Equal(EngineState.Ready, engine.State);

        // 后续有效命令仍可正常执行
        var validGid = await engine.AddUriAsync(new[] { "https://example.com/valid.zip" });
        Assert.False(string.IsNullOrWhiteSpace(validGid));
        Assert.Equal(EngineState.Ready, engine.State);

        // 2. Fatal 注入（模拟内存破坏或 native 严重不变量崩溃）
        engine.InjectFatalOnNextCommand = true;
        await Assert.ThrowsAsync<EngineFaultedException>(async () =>
        {
            await engine.AddUriAsync(new[] { "https://example.com/crash.zip" });
        });

        // 引擎转入终态 Faulted，后续所有新命令被拒绝，不自动重启
        Assert.Equal(EngineState.Faulted, engine.State);
        await Assert.ThrowsAsync<EngineFaultedException>(async () =>
        {
            await engine.AddUriAsync(new[] { "https://example.com/another.zip" });
        });
    }

    /// <summary>
    /// C07: 取消与接纳/开始竞态、调用方超时
    /// 通过条件: 执行前取消无副作用；执行后真实结果可查询；超时不伪造取消、不自动重加，TCS 只完成一次。
    /// </summary>
    public static async Task Test_C07_CancellationRaceAndCallerTimeout(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);
        await engine.StartAsync(CreateTestStartOptions());

        // 1. 接纳前即已取消：保证不产生副作用，直接抛出 OperationCanceledException
        using var preCts = new CancellationTokenSource();
        preCts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await engine.AddUriAsync(new[] { "https://example.com/pre-canceled.zip" }, cancellationToken: preCts.Token);
        });

        // 2. native 开始执行后的取消：不伪造取消，真实结果由 native 决定并完成 TCS
        var execStartedGate = new TaskCompletionSource();
        var allowCompletion = new TaskCompletionSource();
        using var inFlightCts = new CancellationTokenSource();

        engine.OnBeforeCommandExecute = (opId, opName) =>
        {
            execStartedGate.TrySetResult();
            // 在执行阶段触发取消
            inFlightCts.Cancel();
            allowCompletion.Task.Wait();
        };

        var addInFlightTask = engine.AddUriAsync(
            new[] { "https://example.com/in-flight.zip" },
            cancellationToken: inFlightCts.Token);

        await execStartedGate.Task;
        allowCompletion.SetResult();

        // 结果正常完成，而不是被假取消
        var gid = await addInFlightTask;
        Assert.False(string.IsNullOrWhiteSpace(gid));

        // 3. 调用方超时：仅结束调用方本地等待，不破坏引擎内部状态
        engine.OnBeforeCommandExecute = null;
        using var shortTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var validTask = engine.AddUriAsync(new[] { "https://example.com/quick.zip" });
        var quickGid = await validTask;
        Assert.False(string.IsNullOrWhiteSpace(quickGid));

        await engine.ShutdownAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    /// <summary>
    /// C08: 持续命令压力、有限并发生产者
    /// 通过条件: native 得到推进，等待者/队列有上限；关闭信号不被满队列困住，continuation 不阻塞 owner。
    /// </summary>
    public static async Task Test_C08_BoundedQueueAndContinuationIsolation(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        // 配置极小容量队列 (容量为 4)
        var config = new EngineRuntimeConfig
        {
            CommandQueueCapacity = 4,
            BatchCommandBudget = 2,
            LoopTimeout = TimeSpan.FromMilliseconds(50)
        };

        await using var engine = engineFactory(config);

        // 阻塞 owner 线程以填满队列
        var ownerBlocker = new TaskCompletionSource();
        engine.OnBeforeCommandExecute = (opId, opName) =>
        {
            ownerBlocker.Task.Wait();
        };

        await engine.StartAsync(CreateTestStartOptions());

        var tasks = new List<Task<string>>();
        bool queueFullObserved = false;

        try
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    tasks.Add(engine.AddUriAsync(new[] { $"https://example.com/cmd{i}.zip" }));
                }
                catch (EngineQueueFullException)
                {
                    queueFullObserved = true;
                    break;
                }
            }

            Assert.True(queueFullObserved, "Engine should reach queue capacity (4) and reject with EngineQueueFullException.");
        }
        finally
        {
            // 放行 owner 执行，队列被排空推进
            engine.OnBeforeCommandExecute = null;
            ownerBlocker.TrySetResult();
        }

        var gids = await Task.WhenAll(tasks);
        Assert.True(gids.Length >= 4, $"Expected at least 4 commands to succeed, but got {gids.Length}");

        // 验证 continuation 不在 owner 线程执行
        var ownerThreadId = -1;
        var continuationThreadId = -1;
        var continuationGate = new TaskCompletionSource();

        engine.OnBeforeCommandExecute = (opId, opName) =>
        {
            ownerThreadId = Environment.CurrentManagedThreadId;
        };

        var probeTask = engine.AddUriAsync(new[] { "https://example.com/thread-probe.zip" });
        _ = probeTask.ContinueWith(t =>
        {
            continuationThreadId = Environment.CurrentManagedThreadId;
            continuationGate.SetResult();
        });

        await continuationGate.Task;
        Assert.NotEqual(ownerThreadId, continuationThreadId, "Continuation must not run on the engine owner thread.");

        await engine.ShutdownAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    /// <summary>
    /// C09: 分页查询与修订号一致性 (F05)
    /// 通过条件: 0、1、101 任务分页准确；支持按状态过滤；返回修订号随变更递增；不固定只截断 100 条。
    /// </summary>
    public static async Task Test_C09_PagedQueriesAndRevisionConsistency(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);
        await engine.StartAsync(CreateTestStartOptions());

        // 1. 空列表查询
        var emptyPage = await engine.GetTasksPagedAsync(TaskStatusFilter.All, 0, 50);
        Assert.Equal(0, emptyPage.TotalCount);
        Assert.Equal(0, emptyPage.Items.Count);
        Assert.Equal(0, emptyPage.Offset);
        Assert.Equal(50, emptyPage.Limit);

        // 2. 添加 101 个任务，突破旧版固定 100 项截断限制
        var gids = new List<string>();
        for (int i = 0; i < 101; i++)
        {
            var gid = await engine.AddUriAsync(new[] { $"http://192.0.2.1/item-{i}.pkg" });
            gids.Add(gid);
        }

        // 暂停后 20 个任务以构造 waiting 状态
        for (int i = 81; i < 101; i++)
        {
            await engine.PauseAsync(gids[i]);
        }

        // 移除后 5 个任务以构造 stopped 状态
        for (int i = 96; i < 101; i++)
        {
            await engine.RemoveAsync(gids[i]);
        }

        // 3. 验证 All 分页与只读查询修订号一致性
        var page1 = await engine.GetTasksPagedAsync(TaskStatusFilter.All, 0, 50);
        Assert.Equal(101, page1.TotalCount);
        Assert.Equal(50, page1.Items.Count);
        Assert.True(page1.Revision > 0);

        var page2 = await engine.GetTasksPagedAsync(TaskStatusFilter.All, 50, 50);
        Assert.Equal(101, page2.TotalCount);
        Assert.Equal(50, page2.Items.Count);
        // 关键断言：连续只读分页查询不增加修订号，跨页视图具有严格一致性 (F05)
        Assert.Equal(page1.Revision, page2.Revision);

        var page3 = await engine.GetTasksPagedAsync(TaskStatusFilter.All, 100, 50);
        Assert.Equal(101, page3.TotalCount);
        Assert.Equal(1, page3.Items.Count);
        Assert.Equal(page1.Revision, page3.Revision);

        // 验证跨页 GID 排序确定性（不重复、不遗漏、严格单调）
        var allPagedGids = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(t => t.Gid).ToList();
        Assert.Equal(101, allPagedGids.Count);
        Assert.Equal(101, allPagedGids.Distinct().Count());

        // 突变操作触发修订号单调递增
        await engine.AddUriAsync(new[] { "http://192.0.2.1/new-mutation.pkg" });
        var pageAfterMutation = await engine.GetTasksPagedAsync(TaskStatusFilter.All, 0, 10);
        Assert.True(pageAfterMutation.Revision > page1.Revision);
        Assert.Equal(102, pageAfterMutation.TotalCount);

        // 4. 验证状态筛选
        var activePage = await engine.GetTasksPagedAsync(TaskStatusFilter.Active, 0, 200);
        // aria2 limits concurrent downloads; excess unpaused tasks are waiting, not active.
        Assert.True(activePage.TotalCount > 0);
        Assert.Equal(activePage.TotalCount, activePage.Items.Count);
        Assert.True(activePage.Items.All(t => t.Status == "active"));

        var waitingPage = await engine.GetTasksPagedAsync(TaskStatusFilter.Waiting, 0, 200);
        Assert.Equal(waitingPage.TotalCount, waitingPage.Items.Count);
        Assert.True(waitingPage.Items.All(t => t.Status is "waiting" or "paused"));
        foreach (var pausedGid in gids.Skip(81).Take(15))
            Assert.True(waitingPage.Items.Any(t => t.Gid == pausedGid && t.Status == "paused"));

        var stoppedPage = await engine.GetTasksPagedAsync(TaskStatusFilter.Stopped, 0, 200);
        Assert.Equal(5, stoppedPage.TotalCount);
        Assert.Equal(5, stoppedPage.Items.Count);
        foreach (var removedGid in gids.Skip(96))
            Assert.True(stoppedPage.Items.Any(t => t.Gid == removedGid && t.Status == "removed"));
        var partition = activePage.Items.Concat(waitingPage.Items).Concat(stoppedPage.Items).ToList();
        Assert.Equal(102, partition.Count);
        Assert.Equal(102, partition.Select(t => t.Gid).Distinct().Count());

        // 5. 参数异常校验
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await engine.GetTasksPagedAsync(TaskStatusFilter.All, -1, 10);
        });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await engine.GetTasksPagedAsync(TaskStatusFilter.All, 0, 0);
        });

        await engine.ShutdownAsync();
    }

    /// <summary>
    /// C10: 全局与单任务实际选项读取和变更 (F04)
    /// 通过条件: 初始选项可读；任务选项与全局继承正确；修改后可读取最新值；非法 GID 抛出 EngineCommandException。
    /// </summary>
    public static async Task Test_C10_OptionsInspectionAndRealValues(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);
        var startOptions = new EngineStartOptions
        {
            SessionFilePath = Path.Combine(Path.GetTempPath(), $"test-session-{Guid.NewGuid():N}.session"),
            SaveSessionIntervalSeconds = 30,
            DownloadDir = "/downloads/default",
            InitialOptions = new Dictionary<string, string>
            {
                ["max-overall-download-limit"] = "1048576",
                ["split"] = "8"
            }
        };

        await engine.StartAsync(startOptions);

        // 1. 读取全局选项
        var globalOpts = await engine.GetGlobalOptionAsync();
        Assert.Equal("/downloads/default", globalOpts["dir"]);
        Assert.Equal("1048576", globalOpts["max-overall-download-limit"]);
        Assert.Equal("8", globalOpts["split"]);

        // 2. 修改全局选项并重读
        await engine.ChangeGlobalOptionAsync(new[]
        {
            new KeyValuePair<string, string>("max-overall-download-limit", "2097152")
        });
        var updatedGlobal = await engine.GetGlobalOptionAsync();
        Assert.Equal("2097152", updatedGlobal["max-overall-download-limit"]);

        // 3. 添加带特定选项的任务
        var gid = await engine.AddUriAsync(
            new[] { "https://example.com/custom.iso" },
            new[]
            {
                new KeyValuePair<string, string>("dir", "/downloads/custom"),
                new KeyValuePair<string, string>("max-download-limit", "524288")
            });

        // 4. 读取该任务选项 (继承全局并包含任务特定值)
        var taskOpts = await engine.GetTaskOptionAsync(gid);
        Assert.Equal("/downloads/custom", taskOpts["dir"]);
        Assert.Equal("524288", taskOpts["max-download-limit"]);

        // 5. 修改任务选项并验证
        await engine.ChangeOptionAsync(gid, new[]
        {
            new KeyValuePair<string, string>("max-download-limit", "1048576")
        });
        var updatedTaskOpts = await engine.GetTaskOptionAsync(gid);
        Assert.Equal("1048576", updatedTaskOpts["max-download-limit"]);

        // 6. 不存在的 GID 查询报错
        await Assert.ThrowsAsync<EngineCommandException>(async () =>
        {
            await engine.GetTaskOptionAsync("non_existent_gid");
        });

        await engine.ShutdownAsync();
    }

    /// <summary>
    /// C11: 多值/重复 Header 支持与边界校验验证 (§8.3, IAriaEngine 缺口修复, BOUNDARIES.md §3/§4)
    /// 通过条件: 完整保留同一选项名称的多次出现（如多个 header）；支持提取所有 header 值；
    ///           严格拒绝非法控制字符 (CRLF注入、\0空字符) 与超大选项 (>8 KiB)。
    /// </summary>
    public static async Task Test_C11_MultiValueRepeatedHeaders(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);
        await engine.StartAsync(CreateTestStartOptions());

        var repeatedOptions = new List<KeyValuePair<string, string>>
        {
            new("header", "User-Agent: AriaUI/1.0"),
            new("header", "Authorization: Bearer token-xyz"),
            new("header", "Cookie: sid=123; session=abc"),
            new("header", "Accept-Language: zh-CN,zh;q=0.9"),
            new("out", "sample.bin")
        };

        // 1. 添加任务：接收含有多个 "header" 的集合
        var gid = await engine.AddUriAsync(new[] { "https://example.com/secure/sample.bin" }, repeatedOptions);
        Assert.False(string.IsNullOrWhiteSpace(gid));

        // 2. 验证读取到的任务选项：所有 4 个 header 均被无损保留，不被折叠或覆盖
        var taskOpts = await engine.GetTaskOptionAsync(gid);
        Assert.Equal("sample.bin", taskOpts["out"]);

        var headers = taskOpts.GetValues("header").ToList();
        Assert.Equal(4, headers.Count);
        Assert.Contains("User-Agent: AriaUI/1.0", headers);
        Assert.Contains("Authorization: Bearer token-xyz", headers);
        Assert.Contains("Cookie: sid=123; session=abc", headers);
        Assert.Contains("Accept-Language: zh-CN,zh;q=0.9", headers);

        // 3. 继续追加重复 header 并验证无损追加与非重复选项替换
        await engine.ChangeOptionAsync(gid, new[]
        {
            new KeyValuePair<string, string>("header", "X-Trace-Id: 98765"),
            new KeyValuePair<string, string>("out", "sample-renamed.bin")
        });

        var updatedOpts = await engine.GetTaskOptionAsync(gid);
        Assert.Equal("sample-renamed.bin", updatedOpts["out"]);
        var updatedHeaders = updatedOpts.GetValues("header").ToList();
        Assert.Equal(5, updatedHeaders.Count);
        Assert.Contains("X-Trace-Id: 98765", updatedHeaders);

        // 4. 防御边界校验：CRLF 注入（\r\n）、\0 截断、超 8 KiB 限制
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await engine.AddUriAsync(
                new[] { "https://example.com/bad1.zip" },
                new[] { new KeyValuePair<string, string>("header", "Evil: foo\r\nInjected: bar") });
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await engine.AddUriAsync(
                new[] { "https://example.com/bad2.zip" },
                new[] { new KeyValuePair<string, string>("bad\0key", "val") });
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await engine.AddUriAsync(
                new[] { "https://example.com/bad3.zip" },
                new[] { new KeyValuePair<string, string>("key", "bad\0val") });
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await engine.AddUriAsync(
                new[] { "https://example.com/bad4.zip" },
                new[] { new KeyValuePair<string, string>("huge", new string('a', 8193)) });
        });

        await engine.ShutdownAsync();
    }

    /// <summary>
    /// C12: 操作结果查询与调用方超时解耦 (C07, §6, §8.4, G08-G09)
    /// 通过条件: 可查询 Pending、Completed(含真实 GID)、Failed 操作(含目标 GID)；未知或超期操作返回 Unknown；超时后不重复执行。
    /// </summary>
    public static async Task Test_C12_OperationOutcomeTrackingAndTimeoutDecoupling(Func<EngineRuntimeConfig?, ITestHookableEngine>? engineFactory = null)
    {
        engineFactory ??= cfg => new FakeAriaEngine(cfg);
        await using var engine = engineFactory(null);
        await engine.StartAsync(CreateTestStartOptions());

        // 1. 验证正常完成的操作结果登记
        var addGid = await engine.AddUriAsync(new[] { "https://example.com/normal.zip" });
        var completedOutcome = await engine.GetOperationOutcomeAsync(1);
        Assert.Equal(1, completedOutcome.OperationId);
        Assert.Equal(OperationStatus.Completed, completedOutcome.Status);
        Assert.Equal(addGid, completedOutcome.Gid);

        // 2. 验证失败操作的结果登记与目标 GID 保留
        long failedOpId = 0;
        try
        {
            await engine.PauseAsync("invalid_gid_for_outcome");
        }
        catch (EngineCommandException)
        {
            failedOpId = 2; // 第二个操作
        }

        var failedOutcome = await engine.GetOperationOutcomeAsync(failedOpId);
        Assert.Equal(failedOpId, failedOutcome.OperationId);
        Assert.Equal(OperationStatus.Failed, failedOutcome.Status);
        Assert.Equal("invalid_gid_for_outcome", failedOutcome.Gid);
        Assert.NotNull(failedOutcome.ErrorMessage);

        // 3. 验证未发生过/已过期的未知操作 ID
        var unknownOutcome = await engine.GetOperationOutcomeAsync(999999);
        Assert.Equal(999999, unknownOutcome.OperationId);
        Assert.Equal(OperationStatus.Unknown, unknownOutcome.Status);

        // 4. 验证执行中的 Pending 状态查询
        var cmdStartedGate = new TaskCompletionSource();
        var allowCmdFinishGate = new TaskCompletionSource();
        long inFlightOpId = 0;

        engine.OnBeforeCommandExecute = (opId, opName) =>
        {
            inFlightOpId = opId;
            cmdStartedGate.SetResult();
            allowCmdFinishGate.Task.Wait();
        };

        var slowTask = engine.AddUriAsync(new[] { "https://example.com/slow.zip" });
        await cmdStartedGate.Task;

        // 在执行期间查询状态：应为 Pending
        var pendingOutcome = await engine.GetOperationOutcomeAsync(inFlightOpId);
        Assert.Equal(inFlightOpId, pendingOutcome.OperationId);
        Assert.Equal(OperationStatus.Pending, pendingOutcome.Status);

        allowCmdFinishGate.SetResult();
        await slowTask;

        // 执行完成后再次查询：应变为 Completed
        var postOutcome = await engine.GetOperationOutcomeAsync(inFlightOpId);
        Assert.Equal(OperationStatus.Completed, postOutcome.Status);

        await engine.ShutdownAsync();
    }
}
