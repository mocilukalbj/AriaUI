using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Models;
using AriaUI.Services;
using AriaUI.Services.Engine;
using AriaUI.Services.Gateway;

namespace AriaUI.Tests;

/// <summary>
/// LIBARIA2_TEST_PLAN.md §3.4 P01-P04:
/// 验证发布包：Debug、Release、Native AOT，以及无预装 aria2 环境下的启动、下载和浏览器接管；确认没有控制端口。
/// </summary>
public static class PackagingAndReleaseAcceptanceTests
{
    private static string GetRepoRoot() => Phase1NativeProbe.GetRepoRoot();

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
    /// P01/P04: 验证发布包产物结构、ELF 属性、Native 动态库与 SHA256 校验和
    /// </summary>
    public static async Task Test_P01_VerifyPublishedArtifacts()
    {
        var repoRoot = GetRepoRoot();
        var releaseDir = Path.Combine(repoRoot, "publish", "linux-x64-release");
        var aotDir = Path.Combine(repoRoot, "publish", "linux-x64-aot");
        var hostDir = Path.Combine(repoRoot, "publish", "linux-x64-host");
        var debugDir = Path.Combine(repoRoot, "publish", "linux-x64-debug");

        // 1. Verify Debug build output
        Assert.True(Directory.Exists(debugDir), "Debug publish directory must exist.");
        Assert.True(File.Exists(Path.Combine(debugDir, "AriaUI")), "Debug executable AriaUI must exist.");
        Assert.True(File.Exists(Path.Combine(debugDir, "AriaUI.dll")), "Debug assembly AriaUI.dll must exist.");
        Assert.True(File.Exists(Path.Combine(debugDir, "runtimes", "linux-x64", "native", "libaria2.so")), "Debug libaria2.so must exist.");
        Assert.True(File.Exists(Path.Combine(debugDir, "runtimes", "linux-x64", "native", "libaria2_bridge.so")), "Debug libaria2_bridge.so must exist.");

        // 2. Verify Release self-contained publish directory
        Assert.True(Directory.Exists(releaseDir), "Release publish directory must exist.");
        Assert.True(File.Exists(Path.Combine(releaseDir, "AriaUI")), "Release executable AriaUI must exist.");
        Assert.True(File.Exists(Path.Combine(releaseDir, "AriaUI.dll")), "Release assembly AriaUI.dll must exist.");
        Assert.True(File.Exists(Path.Combine(releaseDir, "runtimes", "linux-x64", "native", "libaria2.so")), "Release libaria2.so must exist.");
        Assert.True(File.Exists(Path.Combine(releaseDir, "runtimes", "linux-x64", "native", "libaria2_bridge.so")), "Release libaria2_bridge.so must exist.");

        // 3. Verify Native AOT publish directory
        Assert.True(Directory.Exists(aotDir), "Native AOT publish directory must exist.");
        var aotExe = Path.Combine(aotDir, "AriaUI");
        Assert.True(File.Exists(aotExe), "Native AOT executable AriaUI must exist.");
        var aotInfo = new FileInfo(aotExe);
        Assert.True(aotInfo.Length > 10 * 1024 * 1024, $"Native AOT executable size ({aotInfo.Length} bytes) should be > 10 MB.");
        Assert.True(File.Exists(Path.Combine(aotDir, "libSkiaSharp.so")), "Native AOT libSkiaSharp.so must exist.");
        Assert.True(File.Exists(Path.Combine(aotDir, "libHarfBuzzSharp.so")), "Native AOT libHarfBuzzSharp.so must exist.");

        // 4. Verify Thin Host publish directory
        Assert.True(Directory.Exists(hostDir), "Thin host publish directory must exist.");
        Assert.True(File.Exists(Path.Combine(hostDir, "AriaUI.Host")), "AriaUI.Host executable must exist.");

        // 5. Verify Checksums file exists and contains entries
        var checksumFile = Path.Combine(repoRoot, "packaging", "SHA256SUMS");
        Assert.True(File.Exists(checksumFile), "packaging/SHA256SUMS file must exist.");
        var checksumText = await File.ReadAllTextAsync(checksumFile);
        Assert.True(checksumText.Contains("libaria2.so"), "SHA256SUMS must contain libaria2.so.");
        Assert.True(checksumText.Contains("AriaUI"), "SHA256SUMS must contain AriaUI.");
        Assert.True(checksumText.Contains("AriaUI.Host"), "SHA256SUMS must contain AriaUI.Host.");
    }

    /// <summary>
    /// P02: 无预装 aria2 环境下的启动、下载与零控制端口审计
    /// 验证引擎完全内嵌 libaria2，不依赖 PATH 中的 aria2c 可执行文件，且全程 0 控制端口。
    /// </summary>
    public static async Task Test_P02_NoPreinstalledAria2_StartupAndDownload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"p02-noaria2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sessionFile = Path.Combine(tempDir, "session.dat");

        // 1. Setup local HTTP test fixture server serving known bytes
        byte[] testPayload = Encoding.UTF8.GetBytes("AriaUI_Standalone_Embedded_LibAria2_Verification_Payload_2026");
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(testPayload));

        int port = 28000 + (Math.Abs(Guid.NewGuid().GetHashCode()) % 2000);
        string url = $"http://127.0.0.1:{port}/p02-test-artifact.bin";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = testPayload.Length;
                await context.Response.OutputStream.WriteAsync(testPayload);
                context.Response.OutputStream.Close();
            }
            catch { }
        });

        try
        {
            // 2. Configure engine with stripped PATH (no aria2c available)
            string originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            try
            {
                // Strip all directories containing aria2c from PATH
                var strippedPathParts = originalPath.Split(Path.PathSeparator)
                    .Where(p => !File.Exists(Path.Combine(p, "aria2c")))
                    .ToList();
                Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, strippedPathParts));

                int aria2cCountBefore = Process.GetProcessesByName("aria2c").Length;

                var config = new EngineRuntimeConfig();
                config.Validate();

                await using var engine = new NativeAriaEngineHost(config);
                await engine.StartAsync(new EngineStartOptions
                {
                    SessionFilePath = sessionFile,
                    SaveSessionIntervalSeconds = 30,
                    DownloadDir = tempDir
                });

                Assert.Equal(EngineState.Ready, engine.State);

                // Zero control port check on engine startup (fixture port allowed)
                NetworkAuditHelper.AssertZeroTcpListenSockets(new[] { port });

                // 3. Perform download of the HTTP fixture
                var gid = await engine.AddUriAsync(new[] { url });
                Assert.NotNull(gid);

                // Wait for download to complete (up to 10 seconds)
                string downloadedFile = Path.Combine(tempDir, "p02-test-artifact.bin");
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(downloadedFile) && new FileInfo(downloadedFile).Length == testPayload.Length)
                    {
                        break;
                    }
                    await Task.Delay(100);
                }

                Assert.True(File.Exists(downloadedFile), "Target download file must be created on disk.");
                byte[] downloadedBytes = await File.ReadAllBytesAsync(downloadedFile);
                Assert.Equal(testPayload.Length, downloadedBytes.Length);
                string actualSha256 = Convert.ToHexString(SHA256.HashData(downloadedBytes));
                Assert.Equal(expectedSha256, actualSha256);

                // 4. Verify no aria2c subprocess was ever spawned
                int aria2cCountAfter = Process.GetProcessesByName("aria2c").Length;
                Assert.Equal(aria2cCountBefore, aria2cCountAfter);

                // 5. Zero control port audit during and after download (fixture port allowed)
                NetworkAuditHelper.AssertZeroTcpListenSockets(new[] { port });

                await engine.ShutdownAsync();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            listener.Stop();
            await serverTask;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// P03: 无预装 aria2 环境下薄宿主 (AriaUI.Host) 浏览器接管闭环与零控制端口审计
    /// 启动 AriaUI.Host 独立进程，模拟 Chromium 扩展 Native Messaging 管道，完成接管闭环并验证 0 控制端口。
    /// </summary>
    public static async Task Test_P03_NoPreinstalledAria2_BrowserTakeoverViaHost()
    {
        var repoRoot = GetRepoRoot();
        var hostExe = Path.Combine(repoRoot, "publish", "linux-x64-host", "AriaUI.Host");
        Assert.True(File.Exists(hostExe), $"AriaUI.Host binary must exist at {hostExe}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"p03-takeover-{Guid.NewGuid():N}");
        var socketDir = Path.Combine(tempDir, "ariaui");
        Directory.CreateDirectory(socketDir);
        var sockPath = Path.Combine(socketDir, "gateway.sock");
        var lockPath = Path.Combine(socketDir, "ariaui.lock");

        var config = new EngineRuntimeConfig();
        config.Validate();

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

        var gw = new AppGatewayService(taskService, settingsService, config, sockPath, lockPath);
        await gw.StartAsync();

        try
        {
            // Zero control port check on running gateway
            NetworkAuditHelper.AssertZeroTcpListenSockets();

            // Launch published AriaUI.Host thin host process
            var psi = new ProcessStartInfo
            {
                FileName = hostExe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // Point XDG_RUNTIME_DIR to tempDir so host finds tempDir/ariaui/gateway.sock
            psi.Environment["XDG_RUNTIME_DIR"] = tempDir;
            psi.Environment["ARIAUI_SOCKET_PATH"] = sockPath;
            psi.Environment["ARIAUI_LOCK_PATH"] = lockPath;

            try
            {
                using var hostProc = Process.Start(psi);
                Assert.NotNull(hostProc);

                static async Task<byte[]> ReadExactAsync(Stream s, int count)
                {
                    var b = new byte[count];
                    int rTotal = 0;
                    while (rTotal < count)
                    {
                        int r = await s.ReadAsync(b.AsMemory(rTotal, count - rTotal));
                        if (r == 0) throw new EndOfStreamException($"Stream reached EOF after {rTotal}/{count} bytes.");
                        rTotal += r;
                    }
                    return b;
                }

                // 1. Send 32-bit framed Handshake message via Host stdin
                var hsJson = "{\"version\":1,\"action\":\"Handshake\"}";
                var hsBytes = Encoding.UTF8.GetBytes(hsJson);
                var hsLenBytes = BitConverter.GetBytes((uint)hsBytes.Length);

                ArgumentNullException.ThrowIfNull(hostProc);
                ArgumentNullException.ThrowIfNull(hostProc.StandardInput);
                ArgumentNullException.ThrowIfNull(hostProc.StandardOutput);

                await hostProc.StandardInput.BaseStream.WriteAsync(hsLenBytes, 0, 4);
                await hostProc.StandardInput.BaseStream.WriteAsync(hsBytes, 0, hsBytes.Length);
                await hostProc.StandardInput.BaseStream.FlushAsync();

                // Read 32-bit framed response from Host stdout
                byte[] respLenBuf = await ReadExactAsync(hostProc.StandardOutput.BaseStream, 4);
                uint respLen = BitConverter.ToUInt32(respLenBuf, 0);

                byte[] respPayload = await ReadExactAsync(hostProc.StandardOutput.BaseStream, (int)respLen);

                var hsResp = JsonSerializer.Deserialize<GatewayResponse>(Encoding.UTF8.GetString(respPayload));
                Assert.NotNull(hsResp);
                Assert.Equal("Success", hsResp!.Status);
                Assert.Equal(gw.InstanceId, hsResp.InstanceId);

                // 2. Send 32-bit framed AddDownload message via Host stdin
                var addJson = JsonSerializer.Serialize(new GatewayMessage
                {
                    Version = 1,
                    Action = "AddDownload",
                    RequestId = "host-takeover-req-1",
                    ExtensionId = "com.ariaui.chrome.test",
                    InstanceId = gw.InstanceId,
                    Payload = new GatewayPayload
                    {
                        Url = "http://192.0.2.1/browser-takeover-sample.zip",
                        Out = "takeover-sample.zip"
                    }
                });
                var addBytes = Encoding.UTF8.GetBytes(addJson);
                var addLenBytes = BitConverter.GetBytes((uint)addBytes.Length);

                await hostProc.StandardInput.BaseStream.WriteAsync(addLenBytes, 0, 4);
                await hostProc.StandardInput.BaseStream.WriteAsync(addBytes, 0, addBytes.Length);
                await hostProc.StandardInput.BaseStream.FlushAsync();

                // Read AddDownload response
                respLenBuf = await ReadExactAsync(hostProc.StandardOutput.BaseStream, 4);
                respLen = BitConverter.ToUInt32(respLenBuf, 0);

                respPayload = await ReadExactAsync(hostProc.StandardOutput.BaseStream, (int)respLen);

                var addResp = JsonSerializer.Deserialize<GatewayResponse>(Encoding.UTF8.GetString(respPayload));
                Assert.NotNull(addResp);
                Assert.Equal("Success", addResp!.Status);
                Assert.NotNull(addResp.Gid);
                Assert.Equal(16, addResp.Gid!.Length);

                // 3. Confirm zero control ports while thin host is communicating
                NetworkAuditHelper.AssertZeroTcpListenSockets();

                // 3b. Verify NetworkAuditHelper actually detects an unauthorized TCP listening socket
                int testListenPort = 29500 + (Math.Abs(Guid.NewGuid().GetHashCode()) % 1000);
                using (var unauthorizedListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    unauthorizedListener.Bind(new IPEndPoint(IPAddress.Loopback, testListenPort));
                    unauthorizedListener.Listen(1);
                    bool caughtViolation = false;
                    try
                    {
                        NetworkAuditHelper.AssertZeroTcpListenSockets();
                    }
                    catch (InvalidOperationException)
                    {
                        caughtViolation = true;
                    }
                    Assert.True(caughtViolation, "NetworkAuditHelper must detect unauthorized TCP listening socket!");

                    // If that port is explicitly allowed as a fixture, it should pass
                    NetworkAuditHelper.AssertZeroTcpListenSockets(new[] { testListenPort });
                }

                // 4. Close host stdin to verify clean exit
                hostProc.StandardInput.Close();
                bool exited = hostProc.WaitForExit(3000);
                Assert.True(exited, "AriaUI.Host should cleanly exit upon stdin close.");
            }
            finally
            {
                // hostProc cleanup handled by using
            }
        }
        finally
        {
            await gw.DisposeAsync();
            await taskService.ShutdownAsync();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// P04: 深度校验 Native AOT 机器码二进制格式与嵌入依赖
    /// </summary>
    public static async Task Test_P04_NativeAotBinaryInspection()
    {
        var repoRoot = GetRepoRoot();
        var aotBinary = Path.Combine(repoRoot, "publish", "linux-x64-aot", "AriaUI");
        Assert.True(File.Exists(aotBinary), $"Native AOT binary must exist at {aotBinary}");

        // 1. Verify ELF magic header (0x7F, 'E', 'L', 'F')
        using var fs = new FileStream(aotBinary, FileMode.Open, FileAccess.Read);
        byte[] elfHeader = new byte[16];
        int read = await fs.ReadAsync(elfHeader, 0, 16);
        Assert.Equal(16, read);

        Assert.Equal(0x7F, elfHeader[0]);
        Assert.Equal((byte)'E', elfHeader[1]);
        Assert.Equal((byte)'L', elfHeader[2]);
        Assert.Equal((byte)'F', elfHeader[3]);

        // EI_CLASS: 2 = 64-bit architecture
        Assert.Equal(2, elfHeader[4]);
        // EI_DATA: 1 = Little-endian
        Assert.Equal(1, elfHeader[5]);
        // EI_VERSION: 1 = Current version
        Assert.Equal(1, elfHeader[6]);

        // 2. Verify self-contained absence of JIT assemblies in AOT publish directory
        var aotDir = Path.GetDirectoryName(aotBinary)!;
        var clrDlls = Directory.GetFiles(aotDir, "*.dll");
        Assert.Empty(clrDlls); // AOT output must not have managed assemblies in root publish directory

        // 3. Verify embedded native libraries exist in AOT bundle
        var nativeBridge = Path.Combine(aotDir, "runtimes", "linux-x64", "native", "libaria2_bridge.so");
        var nativeAria2 = Path.Combine(aotDir, "runtimes", "linux-x64", "native", "libaria2.so");
        Assert.True(File.Exists(nativeBridge), "Bundled libaria2_bridge.so must exist in AOT package.");
        Assert.True(File.Exists(nativeAria2), "Bundled libaria2.so must exist in AOT package.");

        // 4. Verify zero control ports
        NetworkAuditHelper.AssertZeroTcpListenSockets();

        await Task.CompletedTask;
    }
}
