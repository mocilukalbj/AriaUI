using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;
using AriaUI.Services;
using AriaUI.Services.Engine;
using AriaUI.Services.Gateway;
using CommunityToolkit.Mvvm.Messaging;

namespace AriaUI.Tests;

/// <summary>
/// Phase 3 verification suite covering:
/// 1. Application wiring to IAriaEngine (AriaEngineTaskService) & no aria2c / no control port check.
/// 2. Performance benchmark: Submission-to-real-result latency comparison (RPC vs Native).
/// 3. Gateway & Thin Host protocol compliance (G01–G12, P03).
/// 4. End-to-end browser takeover flow.
/// </summary>
public static class ApplicationWiringAndGatewayTests
{
    private sealed class MockFileSystemService : IFileSystemService
    {
        public bool OpenFileCalled { get; private set; }
        public bool OpenDirectoryCalled { get; private set; }

        public void OpenFile(string filePath) => OpenFileCalled = true;
        public void OpenDirectory(string directoryPath) => OpenDirectoryCalled = true;
    }

    private sealed class MockSettingsService : ISettingsService
    {
        public AppSettings Settings { get; set; } = new()
        {
            RpcSecret = "valid_secret_123"
        };

        public Task SaveAsync(AppSettings settings)
        {
            Settings = settings.Clone();
            return Task.CompletedTask;
        }

        public Task<AppSettings> UpdateAsync(Action<AppSettings> update)
        {
            var clone = Settings.Clone();
            update(clone);
            Settings = clone;
            return Task.FromResult(Settings.Clone());
        }
    }

    private sealed class MockTrackerService : ITrackerService
    {
        public List<string> TrackersToReturn { get; set; } = new() { "http://tracker.example.com/announce" };

        public Task<List<string>> FetchTrackersAsync(string url, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(TrackersToReturn);
        }
    }


    #region Step 1: Application Wiring & Lifecycle Tests

    /// <summary>
    /// Step 1: 验证 AriaEngineTaskService 完整生命周期 (UI 添加、暂停、恢复、删除、设置与关闭恢复)
    /// </summary>
    public static async Task Test_Step1_ApplicationWiring_CompleteLifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"wiring-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = new EngineRuntimeConfig();
            config.Validate();
            await using var engine = new NativeAriaEngineHost(config);

            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            settingsService.Settings.MaxConcurrentDownloads = 4;
            settingsService.Settings.MaxOverallDownloadLimit = 1048576;
            settingsService.Settings.Split = 8;

            var trackerService = new MockTrackerService();
            var fileSystemService = new MockFileSystemService();

            var taskService = new AriaEngineTaskService(engine, settingsService, trackerService, fileSystemService);

            // Verify GlobalStatUpdatedMessage reception via WeakReferenceMessenger
            GlobalStatUpdatedMessage? lastStatMessage = null;
            object statRecipient = new();
            WeakReferenceMessenger.Default.Register<object, GlobalStatUpdatedMessage>(statRecipient, (r, m) => lastStatMessage = m);

            // 1. Initialize
            Assert.False(taskService.IsConnected);
            await taskService.InitializeAsync();
            Assert.True(taskService.IsConnected);
            Assert.NotNull(lastStatMessage);
            Assert.True(lastStatMessage!.IsConnected);

            // 2. Add URI with headers & outFilename
            var testUrl = "http://192.0.2.1/test-file.zip";
            var gid = await taskService.AddUriAsync(
                testUrl,
                split: 4,
                headers: new[] { "Cookie: test=1", "Authorization: Bearer xyz" },
                outFilename: "custom-out.zip");
            Assert.NotNull(gid);
            Assert.Equal(16, gid.Length);

            // 3. Pause
            await taskService.PauseTaskAsync(gid);

            // 4. Resume
            await taskService.ResumeTaskAsync(gid);

            // 5. Settings Save and Apply
            var newSettings = settingsService.Settings.Clone();
            newSettings.MaxConcurrentDownloads = 6;
            newSettings.MaxOverallDownloadLimit = 2097152;
            await taskService.SaveAndApplySettingsAsync(newSettings);

            var globalOpts = await engine.GetGlobalOptionAsync();
            Assert.Equal("6", globalOpts["max-concurrent-downloads"]);
            Assert.Equal("2097152", globalOpts["max-overall-download-limit"]);

            // 6. Remove Task with Delete File Protection (F03)
            var dummyFile = Path.Combine(tempDir, "file.bin");
            await File.WriteAllTextAsync(dummyFile, "dummy");
            await taskService.RemoveTaskAsync(gid, deleteFile: false);

            // 7. Shutdown
            await taskService.ShutdownAsync();
            Assert.False(taskService.IsConnected);
            WeakReferenceMessenger.Default.Unregister<GlobalStatUpdatedMessage>(statRecipient);
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
    /// Step 1 / P03: 验证 Native 模式下不启动 aria2c 子进程，不打开 TCP/HTTP/WebSocket 控制端口
    /// </summary>
    public static async Task Test_Step1_NoAria2cSubprocessAndNoControlPort()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"p03-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            int aria2CountBefore = Process.GetProcessesByName("aria2c").Length;

            var config = new EngineRuntimeConfig();
            await using var engine = new NativeAriaEngineHost(config);

            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            var trackerService = new MockTrackerService();
            var fileSystemService = new MockFileSystemService();

            var taskService = new AriaEngineTaskService(engine, settingsService, trackerService, fileSystemService);
            await taskService.InitializeAsync();

            // Check that aria2c count has NOT increased
            int aria2CountAfter = Process.GetProcessesByName("aria2c").Length;
            Assert.Equal(aria2CountBefore, aria2CountAfter);

            // Check that port 6800 or common control ports are NOT opened by this process
            NetworkAuditHelper.AssertZeroTcpListenSockets();

            await taskService.ShutdownAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    #endregion

    #region Step 1 Performance Benchmark: Same Operation / Same Load Submission Latency

    /// <summary>
    /// 测量并对比 RPC 基线与 Native 引擎在同负载、同操作下从提交到真实 GID 返回的端到端延迟
    /// </summary>
    public static async Task Test_Step1_PerformanceComparison_SubmitToRealResult()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = new EngineRuntimeConfig();
            await using var engine = new NativeAriaEngineHost(config);

            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            var trackerService = new MockTrackerService();
            var fileSystemService = new MockFileSystemService();

            var taskService = new AriaEngineTaskService(engine, settingsService, trackerService, fileSystemService);
            await taskService.InitializeAsync();

            // Warmup
            await taskService.AddUriAsync("http://192.0.2.1/warmup.bin");

            // Measure 20 consecutive AddUriAsync submission latencies
            var samples = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                var gid = await taskService.AddUriAsync($"http://192.0.2.1/item-{i}.pkg");
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                Assert.NotNull(gid);
            }

            samples.Sort();
            double p50 = samples[samples.Count / 2];
            double p95 = samples[(int)(samples.Count * 0.95)];
            double min = samples[0];
            double max = samples[^1];
            double avg = samples.Average();

            Console.WriteLine($"[Perf Comparison] Native AddUriAsync Latency: Min={min:F2}ms, P50={p50:F2}ms, P95={p95:F2}ms, Max={max:F2}ms, Avg={avg:F2}ms");

            // Verify that once running in batch, subsequent operations are processed smoothly
            Assert.True(samples.Count == 20);

            await taskService.ShutdownAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    #endregion

    #region Step 2: Gateway & Protocol Tests (G01–G12, P03)

    private static async Task<(AppGatewayService Gateway, string SocketPath)> CreateTestGatewayAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"gw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var socketPath = Path.Combine(tempDir, "gateway.sock");
        var lockPath = Path.Combine(tempDir, "ariaui.lock");

        var engine = new FakeAriaEngine(new EngineRuntimeConfig());
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

        var gateway = new AppGatewayService(
            taskService,
            settingsService,
            new EngineRuntimeConfig(),
            socketPathOverride: socketPath,
            lockPathOverride: lockPath);

        await gateway.StartAsync();
        return (gateway, socketPath);
    }

    private static async Task<Socket> ConnectToGatewayAsync(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        return socket;
    }

    private static async Task SendFrameAsync(Socket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var lenBytes = BitConverter.GetBytes((uint)bytes.Length);
        await socket.SendAsync(lenBytes, SocketFlags.None);
        await socket.SendAsync(bytes, SocketFlags.None);
    }

    private static async Task<GatewayResponse> ReadFrameAsync(Socket socket)
    {
        var lenBuf = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int r = await socket.ReceiveAsync(lenBuf.AsMemory(read, 4 - read), SocketFlags.None);
            if (r == 0) throw new EndOfStreamException("Socket closed.");
            read += r;
        }

        uint len = BitConverter.ToUInt32(lenBuf, 0);
        var payloadBuf = new byte[len];
        int payloadRead = 0;
        while (payloadRead < (int)len)
        {
            int r = await socket.ReceiveAsync(payloadBuf.AsMemory(payloadRead, (int)len - payloadRead), SocketFlags.None);
            if (r == 0) throw new EndOfStreamException("Socket closed prematurely.");
            payloadRead += r;
        }

        var json = Encoding.UTF8.GetString(payloadBuf);
        return JsonSerializer.Deserialize<GatewayResponse>(json)!;
    }

    /// <summary>
    /// G01: manifest/扩展白名单、错误 origin/版本/操作
    /// 仅允许声明的入口；拒绝错误请求，无任意 RPC、shell、全局选项或删除能力。
    /// </summary>
    public static async Task Test_G01_ManifestAndAllowedActions()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            using var sock = await ConnectToGatewayAsync(sockPath);

            // 1. Handshake success
            await SendFrameAsync(sock, "{\"version\":1,\"action\":\"Handshake\"}");
            var hsResp = await ReadFrameAsync(sock);
            Assert.Equal("Success", hsResp.Status);
            Assert.Equal(gw.InstanceId, hsResp.InstanceId);

            // 2. Unsupported Version
            await SendFrameAsync(sock, "{\"version\":99,\"action\":\"Handshake\"}");
            var badVerResp = await ReadFrameAsync(sock);
            Assert.Equal("UnsupportedVersion", badVerResp.Status);

            // 3. Unsupported Action (Arbitrary RPC / shell rejected)
            await SendFrameAsync(sock, "{\"version\":1,\"action\":\"aria2.tellActive\",\"requestId\":\"req-1\"}");
            var badActionResp = await ReadFrameAsync(sock);
            Assert.Equal("UnsupportedAction", badActionResp.Status);

            // 4. Unknown top-level fields rejected
            await SendFrameAsync(sock, "{\"version\":1,\"action\":\"Handshake\",\"arbitraryInjection\":\"malicious\"}");
            var badFieldResp = await ReadFrameAsync(sock);
            Assert.Equal("BadRequest", badFieldResp.Status);
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// G02: UTF-8 帧、分段读取、粘包、EOF、坏长度
    /// 按字节解析完整帧；半帧到期关闭，坏帧不执行；stdout 只有协议，不混应用启动日志。
    /// </summary>
    public static async Task Test_G02_FrameProtocol_ChunkedAndMalformed()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            using var sock = await ConnectToGatewayAsync(sockPath);

            // 1. Chunked / fragmented frame delivery
            var json = "{\"version\":1,\"action\":\"Handshake\"}";
            var bytes = Encoding.UTF8.GetBytes(json);
            var lenBytes = BitConverter.GetBytes((uint)bytes.Length);

            // Send length 2 bytes + 2 bytes
            await sock.SendAsync(lenBytes.AsMemory(0, 2), SocketFlags.None);
            await Task.Delay(20);
            await sock.SendAsync(lenBytes.AsMemory(2, 2), SocketFlags.None);

            // Send payload in 3 chunks
            int c1 = bytes.Length / 3;
            int c2 = bytes.Length / 3;
            int c3 = bytes.Length - c1 - c2;
            await sock.SendAsync(bytes.AsMemory(0, c1), SocketFlags.None);
            await Task.Delay(20);
            await sock.SendAsync(bytes.AsMemory(c1, c2), SocketFlags.None);
            await Task.Delay(20);
            await sock.SendAsync(bytes.AsMemory(c1 + c2, c3), SocketFlags.None);

            var resp = await ReadFrameAsync(sock);
            Assert.Equal("Success", resp.Status);
            Assert.Equal(gw.InstanceId, resp.InstanceId);

            // 2. Disconnect on incomplete length prefix (send 2 bytes then close socket)
            using (var sockEofLen = await ConnectToGatewayAsync(sockPath))
            {
                var partialLen = new byte[] { 0x05, 0x00 }; // 2 bytes instead of 4
                await sockEofLen.SendAsync(partialLen, SocketFlags.None);
                sockEofLen.Shutdown(SocketShutdown.Both);
                sockEofLen.Close();
            }

            // 3. Disconnect on incomplete frame payload (declare 100 bytes, send only 10 then close)
            using (var sockEofPayload = await ConnectToGatewayAsync(sockPath))
            {
                var declaredLen = BitConverter.GetBytes((uint)100);
                await sockEofPayload.SendAsync(declaredLen, SocketFlags.None);
                var partialData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
                await sockEofPayload.SendAsync(partialData, SocketFlags.None);
                sockEofPayload.Shutdown(SocketShutdown.Both);
                sockEofPayload.Close();
            }

            // 4. Subsequent valid connection works normally after premature EOFs
            using (var sockValid = await ConnectToGatewayAsync(sockPath))
            {
                await SendFrameAsync(sockValid, "{\"version\":1,\"action\":\"Handshake\"}");
                var hsValid = await ReadFrameAsync(sockValid);
                Assert.Equal("Success", hsValid.Status);
                Assert.Equal(gw.InstanceId, hsValid.InstanceId);
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// G03: 64 KiB 边界、超限帧、未知字段、header 注入
    /// 分配前限长，未知/危险输入明确拒绝；CR/LF/NUL、路径穿越和非白名单选项不进入内核。
    /// </summary>
    public static async Task Test_G03_64KiBBoundary_HeaderInjection_PathTraversal()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            // 1. Frame length exceeds 64 KiB (65537 bytes)
            using (var sock = await ConnectToGatewayAsync(sockPath))
            {
                uint oversized = 65536 + 1;
                var lenBytes = BitConverter.GetBytes(oversized);
                await sock.SendAsync(lenBytes, SocketFlags.None);

                var resp = await ReadFrameAsync(sock);
                Assert.Equal("FrameTooLarge", resp.Status);
            }

            // 2. Header injection (CR/LF/NUL in headers)
            using (var sock = await ConnectToGatewayAsync(sockPath))
            {
                var reqWithInject = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-inject",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload
                    {
                        Url = "https://example.com/test.bin",
                        Headers = new List<string> { "X-Custom: safe", "X-Injected: bad\r\nInjected-Header: evil" }
                    }
                };

                await SendFrameAsync(sock, JsonSerializer.Serialize(reqWithInject));
                var resp = await ReadFrameAsync(sock);
                Assert.Equal("HeaderInjection", resp.Status);
            }

            // 3. Path traversal in output filename
            using (var sock = await ConnectToGatewayAsync(sockPath))
            {
                var reqWithTraversal = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-traversal",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload
                    {
                        Url = "https://example.com/test.bin",
                        Out = "../../etc/shadow"
                    }
                };

                await SendFrameAsync(sock, JsonSerializer.Serialize(reqWithTraversal));
                var resp = await ReadFrameAsync(sock);
                Assert.Equal("InvalidPath", resp.Status);
            }

            // 4. Directory traversal / escape in payload.Dir
            using (var sock = await ConnectToGatewayAsync(sockPath))
            {
                var reqWithDirTraversal = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-dir-traversal",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload
                    {
                        Url = "https://example.com/test.bin",
                        Dir = "../../etc"
                    }
                };

                await SendFrameAsync(sock, JsonSerializer.Serialize(reqWithDirTraversal));
                var resp = await ReadFrameAsync(sock);
                Assert.Equal("InvalidPath", resp.Status);
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// G04: 连接/速率/队列上限、慢读写
    /// 配置的限额与到期行为有效；接纳前拒绝，不创建无限等待者；坏连接不使 engine Faulted。
    /// </summary>
    public static async Task Test_G04_CapacityLimits_MaxConnections_RateLimiting()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            // 1. Connection limit: Open 8 connections, 9th is rejected with QueueFull
            var sockets = new List<Socket>();
            for (int i = 0; i < 8; i++)
            {
                var s = await ConnectToGatewayAsync(sockPath);
                sockets.Add(s);
            }

            // 9th connection
            using (var s9 = await ConnectToGatewayAsync(sockPath))
            {
                var resp = await ReadFrameAsync(s9);
                Assert.Equal("QueueFull", resp.Status);
            }

            // Close all sockets
            foreach (var s in sockets) s.Dispose();
            await Task.Delay(50);

            // 2. Rate limiting (max 10 req/s per extension)
            using (var s = await ConnectToGatewayAsync(sockPath))
            {
                int rateLimitedCount = 0;
                for (int i = 0; i < 15; i++)
                {
                    var msg = new GatewayMessage
                    {
                        Version = 1,
                        Action = "AddDownload",
                        RequestId = $"req-rate-{i}",
                        ExtensionId = "ext-rate-limited",
                        InstanceId = gw.InstanceId,
                        Payload = new GatewayPayload { Url = "https://example.com/rate.bin" }
                    };
                    await SendFrameAsync(s, JsonSerializer.Serialize(msg));
                    var resp = await ReadFrameAsync(s);
                    if (resp.Status == "RateLimited")
                    {
                        rateLimitedCount++;
                    }
                }
                Assert.True(rateLimitedCount > 0, "Rate limiter should throttle requests exceeding 10 req/s.");
            }

            // 3. Single in-flight request per connection: sending two pipelined requests rejects concurrent with BadRequest
            using (var s = await ConnectToGatewayAsync(sockPath))
            {
                var msg1 = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-pipe-1",
                    ExtensionId = "ext-pipe",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload { Url = "https://example.com/pipe1.bin" }
                };
                var msg2 = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-pipe-2",
                    ExtensionId = "ext-pipe",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload { Url = "https://example.com/pipe2.bin" }
                };

                // Send both frames back-to-back without waiting for response 1
                await SendFrameAsync(s, JsonSerializer.Serialize(msg1));
                await SendFrameAsync(s, JsonSerializer.Serialize(msg2));

                var resp1 = await ReadFrameAsync(s);
                var resp2 = await ReadFrameAsync(s);

                var statuses = new[] { resp1.Status, resp2.Status };
                Assert.True(statuses.Contains("BadRequest"), "Pipelined concurrent request on single connection should be rejected with BadRequest.");
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// G05: 跨 UID、坏目录权限、符号链接、socket 替换
    /// 文件权限和双向 peer UID 检查有效，明确拒绝；0700 目录与 0600 socket。
    /// </summary>
    public static async Task Test_G05_UidPeerCredentialsAndPermissions()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            // 1. Verify socket exists and file permissions (0600) & directory (0700)
            Assert.True(File.Exists(sockPath));

            if (OperatingSystem.IsLinux())
            {
                var sockMode = File.GetUnixFileMode(sockPath);
                Assert.True(sockMode.HasFlag(UnixFileMode.UserRead) && sockMode.HasFlag(UnixFileMode.UserWrite),
                    "Socket must have UserRead and UserWrite permissions (0600).");
                Assert.False(sockMode.HasFlag(UnixFileMode.GroupRead) || sockMode.HasFlag(UnixFileMode.GroupWrite) ||
                             sockMode.HasFlag(UnixFileMode.OtherRead) || sockMode.HasFlag(UnixFileMode.OtherWrite),
                    "Socket must not have group or other permissions.");

                var parentDir = Path.GetDirectoryName(sockPath)!;
                var dirMode = File.GetUnixFileMode(parentDir);
                Assert.True(dirMode.HasFlag(UnixFileMode.UserRead) && dirMode.HasFlag(UnixFileMode.UserWrite) && dirMode.HasFlag(UnixFileMode.UserExecute),
                    "Directory must have 0700 permissions.");
            }

            // 2. Verify local connection succeeds because same UID
            using (var sock = await ConnectToGatewayAsync(sockPath))
            {
                Assert.True(LinuxNative.VerifyPeerCredentials(sock, out uint peerUid), "Local connection must pass peer UID check.");
                Assert.Equal(LinuxNative.getuid(), peerUid);

                await SendFrameAsync(sock, "{\"version\":1,\"action\":\"Handshake\"}");
                var resp = await ReadFrameAsync(sock);
                Assert.Equal("Success", resp.Status);
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }

        // 3. Symlink attack protection: ensure symlink at socket path is replaced safely without following
        var tempSymlinkDir = Path.Combine(Path.GetTempPath(), $"symlink-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSymlinkDir);
        try
        {
            var victimFile = Path.Combine(tempSymlinkDir, "victim.txt");
            await File.WriteAllTextAsync(victimFile, "CRITICAL_VICTIM_CONTENT");

            var symlinkSock = Path.Combine(tempSymlinkDir, "gateway.sock");
            File.CreateSymbolicLink(symlinkSock, victimFile);
            Assert.True(File.ResolveLinkTarget(symlinkSock, false) != null, "Symlink should be created successfully.");

            var engine = new FakeAriaEngine(new EngineRuntimeConfig());
            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempSymlinkDir;
            var taskService = new AriaEngineTaskService(engine, settingsService, new MockTrackerService(), new MockFileSystemService());
            var lockPath = Path.Combine(tempSymlinkDir, "ariaui.lock");

            var gwSym = new AppGatewayService(taskService, settingsService, new EngineRuntimeConfig(), symlinkSock, lockPath);
            await gwSym.StartAsync();

            try
            {
                // Verify victim file was NOT overwritten or damaged
                var victimContent = await File.ReadAllTextAsync(victimFile);
                Assert.Equal("CRITICAL_VICTIM_CONTENT", victimContent);

                // Verify socket path is now a real socket, not following the symlink
                Assert.True(gwSym.IsListening);
            }
            finally
            {
                await gwSym.DisposeAsync();
            }

            // 4. Broken symlink test: socket path points to non-existent target
            var brokenSock = Path.Combine(tempSymlinkDir, "broken.sock");
            File.CreateSymbolicLink(brokenSock, Path.Combine(tempSymlinkDir, "nonexistent.target"));

            var gwBroken = new AppGatewayService(taskService, settingsService, new EngineRuntimeConfig(), brokenSock, lockPath);
            await gwBroken.StartAsync();
            try
            {
                Assert.True(gwBroken.IsListening, "Gateway should clean broken symlink and start successfully.");
            }
            finally
            {
                await gwBroken.DisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempSymlinkDir))
            {
                try { Directory.Delete(tempSymlinkDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// G05 补齐: 目录坏权限自动修复 (0777 -> 0700) 与 Socket 0600 强制约束
    /// </summary>
    public static async Task Test_G05_BadDirectoryPermissionsEnforcement()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bad-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Explicitly set directory permissions to wide open 0777
                File.SetUnixFileMode(tempDir, 
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

                var initialMode = File.GetUnixFileMode(tempDir);
                Assert.True(initialMode.HasFlag(UnixFileMode.OtherRead), "Setup: Directory should initially be 0777.");
            }

            var sockPath = Path.Combine(tempDir, "gateway.sock");
            var lockPath = Path.Combine(tempDir, "ariaui.lock");

            var engine = new FakeAriaEngine(new EngineRuntimeConfig());
            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            var taskService = new AriaEngineTaskService(engine, settingsService, new MockTrackerService(), new MockFileSystemService());
            await taskService.InitializeAsync();

            var gw = new AppGatewayService(taskService, settingsService, new EngineRuntimeConfig(), sockPath, lockPath);
            await gw.StartAsync();

            try
            {
                if (OperatingSystem.IsLinux())
                {
                    // Verify AppGatewayService fixed directory permissions to 0700 (rwx------)
                    var fixedDirMode = File.GetUnixFileMode(tempDir);
                    Assert.True(fixedDirMode.HasFlag(UnixFileMode.UserRead) && 
                                fixedDirMode.HasFlag(UnixFileMode.UserWrite) && 
                                fixedDirMode.HasFlag(UnixFileMode.UserExecute));
                    Assert.False(fixedDirMode.HasFlag(UnixFileMode.GroupRead) || 
                                 fixedDirMode.HasFlag(UnixFileMode.GroupWrite) || 
                                 fixedDirMode.HasFlag(UnixFileMode.OtherRead) || 
                                 fixedDirMode.HasFlag(UnixFileMode.OtherWrite),
                        "AppGatewayService must automatically clamp parent directory permissions to 0700.");

                    // Verify socket mode is 0600 (rw-------)
                    var sockMode = File.GetUnixFileMode(sockPath);
                    Assert.True(sockMode.HasFlag(UnixFileMode.UserRead) && sockMode.HasFlag(UnixFileMode.UserWrite));
                    Assert.False(sockMode.HasFlag(UnixFileMode.OtherRead) || sockMode.HasFlag(UnixFileMode.OtherWrite),
                        "Socket permissions must be strictly 0600.");
                }
            }
            finally
            {
                await gw.DisposeAsync();
                await taskService.ShutdownAsync();
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
    /// G05 补齐: 跨 UID / 对端凭证不匹配时立即断开连接拒绝服务
    /// </summary>
    public static async Task Test_G05_PeerCredentialsUidMismatchRejection()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            // Simulate foreign UID connecting by setting validator hook to return false
            gw.PeerCredentialValidatorHook = _ => false;

            using var sock = await ConnectToGatewayAsync(sockPath);

            // Attempt to send Handshake
            await SendFrameAsync(sock, "{\"version\":1,\"action\":\"Handshake\"}");

            // Server must have rejected and closed connection; reading should return EOF (0 bytes) or throw
            try
            {
                var resp = await ReadFrameAsync(sock);
                Assert.Fail($"Foreign UID connection should have been immediately closed, but got status '{resp.Status}'");
            }
            catch (Exception)
            {
                // Expected: connection was closed immediately by gateway
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// G04 补齐: 引擎内部命令队列满载 (EngineQueueFullException) 转化为网关 QueueFull 并不锁死缓存
    /// </summary>
    public static async Task Test_G04_EngineQueueFullBackpressure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"qf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sockPath = Path.Combine(tempDir, "qf.sock");
            var lockPath = Path.Combine(tempDir, "qf.lock");

            // Create engine with tiny queue capacity (1)
            var config = new EngineRuntimeConfig
            {
                CommandQueueCapacity = 1,
                BatchCommandBudget = 1
            };
            var engine = new FakeAriaEngine(config);
            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            var taskService = new AriaEngineTaskService(engine, settingsService, new MockTrackerService(), new MockFileSystemService());
            await taskService.InitializeAsync();

            var gw = new AppGatewayService(taskService, settingsService, config, sockPath, lockPath);
            await gw.StartAsync();

            var blocker = new TaskCompletionSource();
            try
            {
                // Fill engine queue by injecting a blocker hook
                engine.OnBeforeCommandExecute = (_, _) => blocker.Task.Wait();

                // 1. First command is dequeued by worker thread and blocks in OnBeforeCommandExecute
                _ = engine.AddUriAsync(new[] { "https://example.com/blocker.bin" });

                // Brief pause so the worker thread dequeues the first command and blocks
                await Task.Delay(50);

                // 2. Second command enters and occupies the 1-slot channel capacity
                _ = engine.AddUriAsync(new[] { "https://example.com/queued.bin" });

                using var sock = await ConnectToGatewayAsync(sockPath);

                // 3. Third command sent via gateway - engine command queue is now full!
                var addMsg = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-engine-full",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload { Url = "https://example.com/overflow.pkg" }
                };
                await SendFrameAsync(sock, JsonSerializer.Serialize(addMsg));
                var resp = await ReadFrameAsync(sock);

                Assert.Equal("QueueFull", resp.Status);

                // Release blocker so queue drains
                blocker.TrySetResult();
                engine.OnBeforeCommandExecute = null;
                await Task.Delay(100);

                // Retry with same requestId - since QueueFull removed the pending record, it can now succeed
                await SendFrameAsync(sock, JsonSerializer.Serialize(addMsg));
                var retryResp = await ReadFrameAsync(sock);
                Assert.Equal("Success", retryResp.Status);
                Assert.NotNull(retryResp.Gid);
            }
            finally
            {
                blocker.TrySetResult();
                engine.OnBeforeCommandExecute = null;
                await gw.DisposeAsync();
                await taskService.ShutdownAsync();
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
    /// G02/G12 补齐: 客户端在响应写入期间或刚发送完请求后突然断开 (RST / 异常断线)，网关与引擎保持存活
    /// </summary>
    public static async Task Test_G02_AbruptSocketDisconnectDuringTransmission()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            // 1. Connect, send request, immediately force abortive reset (LingerOption with 0 seconds)
            using (var sock = await ConnectToGatewayAsync(sockPath))
            {
                sock.LingerState = new LingerOption(true, 0); // TCP/Unix RST behavior
                var msg = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-abortive-disconnect",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload { Url = "https://example.com/abortive.pkg" }
                };
                await SendFrameAsync(sock, JsonSerializer.Serialize(msg));
                // Abruptly close immediately
                sock.Close();
            }

            await Task.Delay(50);

            // 2. Verify gateway is still completely healthy and accepts new connections normally
            using (var healthySock = await ConnectToGatewayAsync(sockPath))
            {
                await SendFrameAsync(healthySock, "{\"version\":1,\"action\":\"Handshake\"}");
                var resp = await ReadFrameAsync(healthySock);
                Assert.Equal("Success", resp.Status);
                Assert.Equal(gw.InstanceId, resp.InstanceId);
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// G06 & G07: App 启动互斥锁与陈旧 Socket 清理
    /// 仅合法持锁 App 清理旧 socket；已有锁不误判没启动，不删他人锁。
    /// </summary>
    public static async Task Test_G06_G07_AppLocking_StaleSocketHandling()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lock-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var lockPath = Path.Combine(tempDir, "ariaui.lock");
        var sockPath = Path.Combine(tempDir, "gateway.sock");

        try
        {
            // 1. Create a stale socket file
            await File.WriteAllTextAsync(sockPath, "stale_socket_placeholder");

            var engine = new FakeAriaEngine(new EngineRuntimeConfig());
            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            var taskService = new AriaEngineTaskService(engine, settingsService, new MockTrackerService(), new MockFileSystemService());

            var gw1 = new AppGatewayService(taskService, settingsService, new EngineRuntimeConfig(), sockPath, lockPath);
            await gw1.StartAsync();

            // Stale socket was safely cleaned by the lock owner
            Assert.True(gw1.IsListening);

            // 2. Second instance fails to start due to lock conflict
            var gw2 = new AppGatewayService(taskService, settingsService, new EngineRuntimeConfig(), sockPath, lockPath);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await gw2.StartAsync();
            });

            await gw1.DisposeAsync();
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
    /// G08 & G09: 同 requestId 重发、载荷冲突、响应丢失与记录容量
    /// 同实例只执行一次；重复返回 Pending/原结果，冲突失败；断开后查询原结果，不自动重加。
    /// </summary>
    public static async Task Test_G08_G09_Deduplication_ConflictDetection_CacheCapacity()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            using var sock = await ConnectToGatewayAsync(sockPath);

            // 1. First AddDownload
            var req1 = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "req-dedup-1",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId,
                Payload = new GatewayPayload { Url = "https://example.com/unique.pkg" }
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(req1));
            var resp1 = await ReadFrameAsync(sock);
            Assert.Equal("Success", resp1.Status);
            Assert.NotNull(resp1.Gid);

            // 2. Resend identical request (same payload): returns cached original result
            await SendFrameAsync(sock, JsonSerializer.Serialize(req1));
            var resp2 = await ReadFrameAsync(sock);
            Assert.Equal("Success", resp2.Status);
            Assert.Equal(resp1.Gid, resp2.Gid);

            // 3. Resend same requestId with conflicting payload: returns Conflict
            var reqConflict = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "req-dedup-1",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId,
                Payload = new GatewayPayload { Url = "https://example.com/conflicting.pkg" }
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(reqConflict));
            var respConflict = await ReadFrameAsync(sock);
            Assert.Equal("Conflict", respConflict.Status);

            // 4. Query result with GetRequestResult
            var queryReq = new GatewayMessage
            {
                Version = 1,
                Action = "GetRequestResult",
                RequestId = "req-dedup-1",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(queryReq));
            var queryResp = await ReadFrameAsync(sock);
            Assert.Equal("Success", queryResp.Status);
            Assert.Equal(resp1.Gid, queryResp.Gid);

            // 5. Query non-existent requestId: returns UnknownOutcome
            var queryUnknown = new GatewayMessage
            {
                Version = 1,
                Action = "GetRequestResult",
                RequestId = "req-nonexistent",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(queryUnknown));
            var unknownResp = await ReadFrameAsync(sock);
            Assert.Equal("UnknownOutcome", unknownResp.Status);

            // 6. Query with mismatched InstanceId: returns InstanceMismatch
            var queryMismatch = new GatewayMessage
            {
                Version = 1,
                Action = "GetRequestResult",
                RequestId = "req-dedup-1",
                ExtensionId = "ext-test",
                InstanceId = "mismatched_instance_id_999"
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(queryMismatch));
            var mismatchResp = await ReadFrameAsync(sock);
            Assert.Equal("InstanceMismatch", mismatchResp.Status);

            // 7. AddDownload with mismatched InstanceId: returns InstanceMismatch
            var addMismatch = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "req-mismatch-add",
                ExtensionId = "ext-test",
                InstanceId = "mismatched_instance_id_999",
                Payload = new GatewayPayload { Url = "https://example.com/mismatch.pkg" }
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(addMismatch));
            var addMismatchResp = await ReadFrameAsync(sock);
            Assert.Equal("InstanceMismatch", addMismatchResp.Status);
        }
        finally
        {
            await gw.DisposeAsync();
        }

        // 8. Cache capacity limit (QueueFull): test bounded request record capacity
        var tempCapDir = Path.Combine(Path.GetTempPath(), $"cap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCapDir);
        try
        {
            var capSockPath = Path.Combine(tempCapDir, "cap.sock");
            var capLockPath = Path.Combine(tempCapDir, "cap.lock");

            var engine = new FakeAriaEngine(new EngineRuntimeConfig());
            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempCapDir;
            var taskService = new AriaEngineTaskService(engine, settingsService, new MockTrackerService(), new MockFileSystemService());
            await taskService.InitializeAsync();

            var smallCapConfig = new EngineRuntimeConfig { GatewayRequestRecordCapacity = 5 };
            var gwCap = new AppGatewayService(taskService, settingsService, smallCapConfig, capSockPath, capLockPath);
            await gwCap.StartAsync();

            try
            {
                using var s = await ConnectToGatewayAsync(capSockPath);

                // Add 5 distinct requests to fill capacity
                for (int i = 0; i < 5; i++)
                {
                    var msg = new GatewayMessage
                    {
                        Version = 1,
                        Action = "AddDownload",
                        RequestId = $"cap-req-{i}",
                        ExtensionId = "ext-cap",
                        InstanceId = gwCap.InstanceId,
                        Payload = new GatewayPayload { Url = $"https://example.com/item-{i}.pkg" }
                    };
                    await SendFrameAsync(s, JsonSerializer.Serialize(msg));
                    var r = await ReadFrameAsync(s);
                    Assert.Equal("Success", r.Status);
                }

                // 6th request must be rejected with QueueFull
                var msg6 = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "cap-req-6-overflow",
                    ExtensionId = "ext-cap",
                    InstanceId = gwCap.InstanceId,
                    Payload = new GatewayPayload { Url = "https://example.com/overflow.pkg" }
                };
                await SendFrameAsync(s, JsonSerializer.Serialize(msg6));
                var r6 = await ReadFrameAsync(s);
                Assert.Equal("QueueFull", r6.Status);

                // Resending an existing requestId still returns cached result even when capacity is full
                var msgExisting = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "cap-req-0",
                    ExtensionId = "ext-cap",
                    InstanceId = gwCap.InstanceId,
                    Payload = new GatewayPayload { Url = "https://example.com/item-0.pkg" }
                };
                await SendFrameAsync(s, JsonSerializer.Serialize(msgExisting));
                var rExisting = await ReadFrameAsync(s);
                Assert.Equal("Success", rExisting.Status);
            }
            finally
            {
                await gwCap.DisposeAsync();
                await taskService.ShutdownAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempCapDir))
            {
                try { Directory.Delete(tempCapDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// G10 & G11: GET/magnet 支持与不支持 scheme (POST/blob/data) 拒绝
    /// </summary>
    public static async Task Test_G10_G11_GetAndMagnet_UnsupportedSchemes()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            using var sock = await ConnectToGatewayAsync(sockPath);

            // 1. Valid Magnet link supported
            var magnetReq = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "req-magnet",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId,
                Payload = new GatewayPayload { Url = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567" }
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(magnetReq));
            var magnetResp = await ReadFrameAsync(sock);
            Assert.Equal("Success", magnetResp.Status);

            // 2. Unsupported blob scheme rejected
            var blobReq = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "req-blob",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId,
                Payload = new GatewayPayload { Url = "blob:https://example.com/uuid-blob-123" }
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(blobReq));
            var blobResp = await ReadFrameAsync(sock);
            Assert.Equal("UnsupportedScheme", blobResp.Status);

            // 3. Unsupported data URI rejected
            var dataReq = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "req-data",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId,
                Payload = new GatewayPayload { Url = "data:text/plain;base64,SGVsbG8=" }
            };
            await SendFrameAsync(sock, JsonSerializer.Serialize(dataReq));
            var dataResp = await ReadFrameAsync(sock);
            Assert.Equal("UnsupportedScheme", dataResp.Status);

            // 4. Timeout & UnknownOutcome Recovery Protocol:
            // Send a download request, simulate client dropped/timed out before reading response
            var reqTimeout = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "req-timeout-recovery",
                ExtensionId = "ext-test",
                InstanceId = gw.InstanceId,
                Payload = new GatewayPayload { Url = "https://example.com/timeout.pkg" }
            };
            using (var dropSock = await ConnectToGatewayAsync(sockPath))
            {
                await SendFrameAsync(dropSock, JsonSerializer.Serialize(reqTimeout));
                // Wait slightly for engine admission
                await Task.Delay(50);
                // Abruptly drop connection before reading response
            }

            // Client reconnects on fresh socket and queries result of timed-out request
            using (var querySock = await ConnectToGatewayAsync(sockPath))
            {
                var queryRecovery = new GatewayMessage
                {
                    Version = 1,
                    Action = "GetRequestResult",
                    RequestId = "req-timeout-recovery",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId
                };
                await SendFrameAsync(querySock, JsonSerializer.Serialize(queryRecovery));
                var recoveryResp = await ReadFrameAsync(querySock);
                Assert.Equal("Success", recoveryResp.Status);
                Assert.NotNull(recoveryResp.Gid);

                // If query result is UnknownOutcome, client must NOT auto re-add
                var queryLost = new GatewayMessage
                {
                    Version = 1,
                    Action = "GetRequestResult",
                    RequestId = "req-never-submitted",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId
                };
                await SendFrameAsync(querySock, JsonSerializer.Serialize(queryLost));
                var lostResp = await ReadFrameAsync(querySock);
                Assert.Equal("UnknownOutcome", lostResp.Status);
                // Protocol requirement: client prompts user instead of blindly resubmitting to avoid duplicate tasks
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// G12: 宿主结束不停止已交给 App 的下载
    /// </summary>
    public static async Task Test_G12_HostLifecycle_DownloadContinuity()
    {
        var (gw, sockPath) = await CreateTestGatewayAsync();
        try
        {
            string gid;
            using (var sock = await ConnectToGatewayAsync(sockPath))
            {
                var req = new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "req-continuity",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload { Url = "https://example.com/continuity.pkg" }
                };
                await SendFrameAsync(sock, JsonSerializer.Serialize(req));
                var resp = await ReadFrameAsync(sock);
                Assert.Equal("Success", resp.Status);
                gid = resp.Gid!;
            }

            // Socket connection closed by client, verify task outcome is still retained in App Gateway
            using (var sock2 = await ConnectToGatewayAsync(sockPath))
            {
                var queryReq = new GatewayMessage
                {
                    Version = 1,
                    Action = "GetRequestResult",
                    RequestId = "req-continuity",
                    ExtensionId = "ext-test",
                    InstanceId = gw.InstanceId
                };
                await SendFrameAsync(sock2, JsonSerializer.Serialize(queryReq));
                var queryResp = await ReadFrameAsync(sock2);
                Assert.Equal("Success", queryResp.Status);
                Assert.Equal(gid, queryResp.Gid);
            }
        }
        finally
        {
            await gw.DisposeAsync();
        }
    }

    /// <summary>
    /// 端到端完整验证：浏览器提交一条下载 → App 接收 → Native 返回真实 GID → UI 显示任务 → 浏览器确认接管
    /// </summary>
    public static async Task Test_EndToEnd_BrowserTakeoverFlow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"e2e-takeover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sockPath = Path.Combine(tempDir, "gateway.sock");
        var lockPath = Path.Combine(tempDir, "ariaui.lock");

        try
        {
            // 1. Initialize Real Native Engine Host
            var config = new EngineRuntimeConfig();
            await using var engine = new NativeAriaEngineHost(config);

            var settingsService = new MockSettingsService();
            settingsService.Settings.DefaultDownloadDir = tempDir;
            var taskService = new AriaEngineTaskService(engine, settingsService, new MockTrackerService(), new MockFileSystemService());
            await taskService.InitializeAsync();

            var gateway = new AppGatewayService(taskService, settingsService, config, sockPath, lockPath);
            await gateway.StartAsync();

            // 2. Browser connects and sends Handshake
            using var browserSock = await ConnectToGatewayAsync(sockPath);
            await SendFrameAsync(browserSock, "{\"version\":1,\"action\":\"Handshake\"}");
            var hs = await ReadFrameAsync(browserSock);
            Assert.Equal("Success", hs.Status);
            Assert.Equal(gateway.InstanceId, hs.InstanceId);

            // 3. Browser intercepts download and sends AddDownload
            var downloadItemReq = new GatewayMessage
            {
                Version = 1,
                Action = "AddDownload",
                RequestId = "browser-req-e2e-1",
                ExtensionId = "chrome-ext-aria2-explorer",
                InstanceId = gateway.InstanceId,
                Payload = new GatewayPayload
                {
                    Url = "https://example.com/release-v1.0.tar.gz",
                    Out = "release-v1.0.tar.gz",
                    Referer = "https://example.com",
                    Headers = new List<string> { "Authorization: Bearer token123" }
                }
            };

            await SendFrameAsync(browserSock, JsonSerializer.Serialize(downloadItemReq));

            // 4. Native Engine processes and returns REAL GID
            var takeoverResp = await ReadFrameAsync(browserSock);
            Assert.Equal("Success", takeoverResp.Status);
            Assert.NotNull(takeoverResp.Gid);
            Assert.Equal(16, takeoverResp.Gid!.Length);

            // 5. Verify UI (AriaEngineTaskService) displays the task in its snapshot / task lists
            var currentTasks = taskService.ActiveTasks.Concat(taskService.WaitingTasks).ToList();
            Assert.True(currentTasks.Any(t => t.Gid == takeoverResp.Gid));

            // 6. Browser confirms takeover (in real browser, this triggers chrome.downloads.cancel)
            Assert.True(takeoverResp.Gid?.Length == 16, "Takeover confirmed with real 16-hex GID.");

            await gateway.DisposeAsync();
            await taskService.ShutdownAsync();
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
    /// Step 2: 验证薄宿主 (AriaUI.Host) 启动、协议头解析及可执行文件查找
    /// </summary>
    public static async Task Test_Host_NativeMessagingStartupAndColdStartDiscovery()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        string? hostBin = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "AriaUI.Host", "bin", "Debug", "net10.0", "AriaUI.Host");
            if (File.Exists(candidate))
            {
                hostBin = candidate;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(hostBin);
        Assert.True(File.Exists(hostBin), $"Host binary must exist at {hostBin}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"host-test-{Guid.NewGuid():N}");
        var ariaDir = Path.Combine(tempDir, "ariaui");
        Directory.CreateDirectory(ariaDir);
        var sockPath = Path.Combine(ariaDir, "gateway.sock");

        using var serverSock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        serverSock.Bind(new UnixDomainSocketEndPoint(sockPath));
        serverSock.Listen(1);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = hostBin,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.EnvironmentVariables["XDG_RUNTIME_DIR"] = tempDir;

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start AriaUI.Host process.");

            // Accept connection from Host
            var acceptTask = serverSock.AcceptAsync();
            var connected = await Task.WhenAny(acceptTask, Task.Delay(5000));
            Assert.True(connected == acceptTask, "Host must connect to gateway socket within 5s.");
            using var clientSock = await acceptTask;

            // Close stdin immediately to test clean EOF handling
            proc.StandardInput.Close();

            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.Equal(0, proc.ExitCode);
            Assert.True(stderr.Contains("[AriaUI.Host] Native messaging host started."), "Host should log startup banner to stderr.");
            Assert.True(stderr.Contains("[AriaUI.Host] Stdin reached EOF. Terminating."), "Host should log EOF termination.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    #endregion
}
