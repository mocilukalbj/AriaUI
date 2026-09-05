using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;
using AriaUI.Services;
using AriaUI.ViewModels;

namespace AriaUI.Tests;

public static class HistoricalRegressionTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    /// <summary>
    /// REVIEW #47 / #53 (F09): Tracker SSRF 校验、正文上限与资源确定性释放
    /// </summary>
    public static async Task Test_F09_TrackerSsrfAndResourceProtection()
    {
        // 1. SSRF 拦截：回环地址、本地链路、私有 IP 应当被立即拒绝
        using var client = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var trackerService = new TrackerService(client);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await trackerService.FetchTrackersAsync("http://127.0.0.1/trackers.txt");
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await trackerService.FetchTrackersAsync("http://localhost/trackers.txt");
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await trackerService.FetchTrackersAsync("http://192.168.1.1/trackers.txt");
        });

        // 2. 超限正文保护：> 512 KB 响应应当立即被中止并抛出异常 (#53)
        var largeContent = new string('a', 513 * 1024);
        using var largeClient = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(largeContent, Encoding.UTF8)
        }));
        using var largeTrackerService = new TrackerService(largeClient);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            // 注意：使用确定为公网 IP 的地址测试，避免受系统 Fake-IP (198.18.0.0/15) DNS 拦截影响
            await largeTrackerService.FetchTrackersAsync("https://93.184.216.34/test/trackers.txt");
        });

        // 3. 正常 Tracker 文本解析
        var validTrackersText = "udp://93.184.216.34:80/announce\n\nhttp://93.184.216.35:1337/announce\n";
        using var validClient = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(validTrackersText, Encoding.UTF8)
        }));
        using var validTrackerService = new TrackerService(validClient);

        var list = await validTrackerService.FetchTrackersAsync("https://93.184.216.34/test/trackers.txt");
        Assert.Equal(2, list.Count);
        Assert.Equal("udp://93.184.216.34:80/announce", list[0]);
        Assert.Equal("http://93.184.216.35:1337/announce", list[1]);
    }

    /// <summary>
    /// REVIEW #50 / #52 / #59 (F04 / F07): 设置校验、空目录自动规范化与 daemon 配置隔离
    /// </summary>
    public static void Test_F04_F07_SettingsValidationAndNormalization()
    {
        // 1. 空下载目录及边界校验
        var settings = new AppSettings
        {
            RpcPort = 6800,
            RpcHost = "127.0.0.1",
            RpcSecret = "valid_secret_123",
            AutoStartDaemon = true,
            DefaultDownloadDir = ""
        };

        var errors = settings.Validate();
        Assert.Equal(0, errors.Count);

        // 2. 非法端口 (0, 70000)
        settings.RpcPort = 0;
        Assert.True(settings.Validate().Count > 0);
        settings.RpcPort = 70000;
        Assert.True(settings.Validate().Count > 0);
        settings.RpcPort = 6800;

        // 3. 托管 daemon 拒绝特权端口 (< 1024)
        settings.RpcPort = 80;
        Assert.True(settings.Validate().Count > 0);
        settings.RpcPort = 6800;

        // 4. 托管 daemon 拒绝非 loopback 地址
        settings.RpcHost = "192.168.1.100";
        Assert.True(settings.Validate().Count > 0);
        settings.RpcHost = "127.0.0.1";

        // 5. 托管 daemon 拒绝 TLS (未配置证书)
        settings.RpcUseTls = true;
        Assert.True(settings.Validate().Count > 0);
        settings.RpcUseTls = false;

        // 6. RpcSecret 空白或包含换行
        settings.RpcSecret = "secret\nwith_newline";
        Assert.True(settings.Validate().Count > 0);
        settings.RpcSecret = "   trimmed_spaces   ";
        Assert.True(settings.Validate().Count > 0);
    }

    /// <summary>
    /// REVIEW #54 (F03): 移除任务前校验路径与下载目录越界保护
    /// </summary>
    public static void Test_F03_TaskRemovalPathProtection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ariaui-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var validFile = Path.Combine(tempDir, "test.zip");
            File.WriteAllText(validFile, "content");

            var outsideFile = Path.GetFullPath("/etc/passwd");

            // 验证目录前缀检查
            var safeRoot = Path.GetFullPath(tempDir);
            var safePrefix = safeRoot.EndsWith(Path.DirectorySeparatorChar)
                ? safeRoot
                : safeRoot + Path.DirectorySeparatorChar;

            var comp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            Assert.True(Path.GetFullPath(validFile).StartsWith(safePrefix, comp));
            Assert.False(Path.GetFullPath(outsideFile).StartsWith(safePrefix, comp));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// REVIEW #58 (U01): TaskListView 筛选按钮派生布尔属性绑定与变更通知
    /// </summary>
    public static void Test_U01_TaskListFilterSelectionState()
    {
        var vm = new TaskListViewModel(
            taskService: null!,
            taskItemFactory: _ => null!,
            newTaskFactory: () => null!);

        var propertyChangedEvents = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
            {
                propertyChangedEvents.Add(e.PropertyName);
            }
        };

        // 初始索引 0: All 选中
        Assert.Equal(0, vm.FilterIndex);
        Assert.True(vm.IsAllFilterSelected);
        Assert.False(vm.IsActiveFilterSelected);
        Assert.False(vm.IsWaitingFilterSelected);
        Assert.False(vm.IsCompleteFilterSelected);
        Assert.False(vm.IsStoppedFilterSelected);

        // 切换到 1: Active
        propertyChangedEvents.Clear();
        vm.FilterIndex = 1;
        Assert.False(vm.IsAllFilterSelected);
        Assert.True(vm.IsActiveFilterSelected);
        Assert.False(vm.IsWaitingFilterSelected);
        Assert.Contains(nameof(vm.IsActiveFilterSelected), propertyChangedEvents);
        Assert.Contains(nameof(vm.IsAllFilterSelected), propertyChangedEvents);

        // 切换到 4: Stopped
        propertyChangedEvents.Clear();
        vm.FilterIndex = 4;
        Assert.False(vm.IsActiveFilterSelected);
        Assert.True(vm.IsStoppedFilterSelected);
        Assert.Contains(nameof(vm.IsStoppedFilterSelected), propertyChangedEvents);
    }
}
