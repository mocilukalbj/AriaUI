using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AriaUI.Helpers;
using AriaUI.Models;
using AriaUI.Services;
using AriaUI.Services.Engine;
using AriaUI.Services.Gateway;

namespace AriaUI.Tests;

public static class WindowsPlatformTests
{
    private sealed class TestSettings : ISettingsService
    {
        public AppSettings Settings { get; private set; } = new() { EnableBtTrackers = false };
        public Task SaveAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
        public Task<AppSettings> UpdateAsync(Action<AppSettings> update) { update(Settings); return Task.FromResult(Settings); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task<JsonElement> Exchange(Stream stream, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length));
        await stream.WriteAsync(bytes);
        return await Receive(stream);
    }

    private static async Task<JsonElement> Receive(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, timeout.Token);
        var length = BitConverter.ToInt32(prefix);
        Require(length is > 0 and <= 65536, "Invalid response frame");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, timeout.Token);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static async Task WaitDownload(IAriaEngine engine, string gid)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(40))
        {
            var result = engine.CurrentSnapshot.StoppedTasks.FirstOrDefault(t => t.Gid == gid);
            if (result != null)
            {
                Require(result.Status == "complete", $"Download failed: {result.Status}");
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Download did not finish");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("ARIAUI_TEST_DIR") ?? Path.GetTempPath(), "ariaui-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldData = Environment.GetEnvironmentVariable("ARIAUI_DATA_DIR");
        Environment.SetEnvironmentVariable("ARIAUI_DATA_DIR", Path.Combine(root, "data"));
        var payload = RandomNumberGenerator.GetBytes(256 * 1024);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var http = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(payload, stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) when (!stop.IsCancellationRequested) { }
            }
        });
        try
        {
            NativeAriaEngineHost.ConfigureNativeResolution();
            await using var engine = new NativeAriaEngineHost(new EngineRuntimeConfig());
            var settings = new TestSettings();
            settings.Settings.DefaultDownloadDir = Path.Combine(root, "中文下载");
            using var tracker = new TrackerService();
            using var tasks = new AriaEngineTaskService(engine, settings, tracker, new FileSystemService());
            await tasks.InitializeAsync();
            Require(engine.State == EngineState.Ready, "Native engine not ready");
            Require(File.Exists(Path.Combine(LocalGatewayEndpoint.DataDirectory, "windows-ca-bundle.pem")), "CA bundle missing");
            Console.WriteLine("PASS Windows DLL loading, engine initialization, trusted-root export");

            await using var gateway = new AppGatewayService(tasks, settings);
            await gateway.StartAsync();
            using var pipe = new NamedPipeClientStream(".", gateway.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            var handshake = await Exchange(pipe, new { version = 1, action = "Handshake" });
            Require(handshake.GetProperty("status").GetString() == "Success", "Handshake failed");
            var instance = handshake.GetProperty("instanceId").GetString();
            Console.WriteLine("PASS current-user named-pipe handshake");

            await using (var other = new AppGatewayService(tasks, settings))
            {
                try { await other.StartAsync(); throw new Exception("Second instance acquired lock"); }
                catch (InvalidOperationException) { Console.WriteLine("PASS single-instance lock"); }
            }

            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/fixture";
            var add = new { version = 1, action = "AddDownload", requestId = "windows-http", extensionId = "windows-test", instanceId = instance,
                payload = new { url, @out = "测试文件.bin" } };
            var accepted = await Exchange(pipe, add);
            Require(accepted.GetProperty("status").GetString() == "Success", accepted.ToString());
            var gid = accepted.GetProperty("gid").GetString()!;
            await WaitDownload(engine, gid);
            var downloaded = await File.ReadAllBytesAsync(Path.Combine(settings.Settings.DefaultDownloadDir, "测试文件.bin"));
            Require(payload.SequenceEqual(downloaded), "Downloaded bytes differ");
            Console.WriteLine("PASS native HTTP download through gateway, Unicode paths, byte comparison");

            var duplicate = await Exchange(pipe, add);
            Require(duplicate.GetProperty("gid").GetString() == gid, "Duplicate request added a new task");
            var queried = await Exchange(pipe, new { version = 1, action = "GetRequestResult", requestId = "windows-http", extensionId = "windows-test", instanceId = instance });
            Require(queried.GetProperty("gid").GetString() == gid, "Query returned wrong GID");
            var mismatch = await Exchange(pipe, new { version = 1, action = "GetRequestResult", requestId = "windows-http", extensionId = "windows-test", instanceId = "old-instance" });
            Require(mismatch.GetProperty("status").GetString() == "InstanceMismatch", "Instance mismatch was accepted");
            Console.WriteLine("PASS request deduplication, outcome query, instance isolation");

            var hostPath = Environment.GetEnvironmentVariable("ARIAUI_TEST_HOST");
            if (!string.IsNullOrWhiteSpace(hostPath))
            {
                var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var host = Process.Start(start)!;
                var stderr = host.StandardError.ReadToEndAsync();
                try
                {
                    var msg = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, action = "Handshake" });
                    await host.StandardInput.BaseStream.WriteAsync(BitConverter.GetBytes(msg.Length));
                    await host.StandardInput.BaseStream.WriteAsync(msg);
                    await host.StandardInput.BaseStream.FlushAsync();
                    var response = await Receive(host.StandardOutput.BaseStream);
                    Require(response.GetProperty("instanceId").GetString() == instance, "Thin host connected to wrong gateway");
                    host.StandardInput.Close();
                    await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Require(host.ExitCode == 0, await stderr);
                    Console.WriteLine("PASS real thin-host stdio -> named pipe -> application handshake");
                }
                finally { if (!host.HasExited) host.Kill(entireProcessTree: true); }
            }

            if (args.Contains("--https"))
            {
                var secure = await engine.AddUriAsync(new[] { "https://raw.githubusercontent.com/aria2/aria2/master/README.rst" },
                    new Dictionary<string, string> { ["out"] = "https-readme.txt" });
                await WaitDownload(engine, secure);
                Require(new FileInfo(Path.Combine(settings.Settings.DefaultDownloadDir, "https-readme.txt")).Length > 100, "HTTPS response empty");
                Console.WriteLine("PASS real HTTPS download with certificate verification enabled");
            }
            await gateway.DisposeAsync();
            await tasks.ShutdownAsync();
            Require(engine.State == EngineState.Stopped, "Engine shutdown failed");
            Require(File.Exists(Path.Combine(LocalGatewayEndpoint.DataDirectory, "aria2.session")), "Session file missing");
            Console.WriteLine("PASS graceful shutdown and session persistence");
            Console.WriteLine("Windows integration checks passed. Test files: " + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            stop.Cancel(); listener.Stop();
            try { await http; } catch (OperationCanceledException) { }
            Environment.SetEnvironmentVariable("ARIAUI_DATA_DIR", oldData);
        }
    }
}
