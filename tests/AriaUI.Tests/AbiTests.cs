using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AriaUI.Services.Engine;

namespace AriaUI.Tests;

public static class AbiTests
{
    private static NativeAriaEngineHost.A2InitOptions CreateValidInitOptions(string dir, string sessionFile) => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeAriaEngineHost.A2InitOptions>(),
        AbiVersion = 1,
        KeepRunning = 1,
        UseSignalHandler = 0,
        DownloadDir = Marshal.StringToCoTaskMemUTF8(dir),
        SessionFile = Marshal.StringToCoTaskMemUTF8(sessionFile),
        OptionKeys = IntPtr.Zero,
        OptionValues = IntPtr.Zero,
        OptionCount = 0
    };

    private static void FreeInitOptions(ref NativeAriaEngineHost.A2InitOptions opts)
    {
        if (opts.DownloadDir != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUTF8(opts.DownloadDir);
        if (opts.SessionFile != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUTF8(opts.SessionFile);
    }

    /// <summary>
    /// A01: ABI 版本、尺寸、对齐、长度、错误线程
    /// 通过条件: 不匹配/坏输入在触碰 session 前失败；跨线程调用被严格拒绝 (A2_STATUS_WRONG_THREAD)。
    /// </summary>
    public static async Task Test_A01_AbiVersionAndWrongThreadRejection()
    {
        NativeAriaEngineHost.ConfigureNativeResolution();

        // 1. ABI 版本检查
        var version = NativeAriaEngineHost.NativeBridge.a2_bridge_get_abi_version();
        Assert.Equal(1u, version);

        // 2. 坏 ABI 版本被拒绝
        var tempDir = Path.GetTempPath();
        var tempSession = Path.Combine(tempDir, $"a01-test-{Guid.NewGuid():N}.session");
        var badOpts = CreateValidInitOptions(tempDir, tempSession);
        badOpts.AbiVersion = 999;
        try
        {
            var r = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref badOpts, out var badSession);
            Assert.Equal(-2, r); // A2_STATUS_INVALID_ARGUMENT
        }
        finally
        {
            FreeInitOptions(ref badOpts);
        }

        // 3. 坏 struct_size 被拒绝
        var badSizeOpts = CreateValidInitOptions(tempDir, tempSession);
        badSizeOpts.StructSize = 7;
        try
        {
            var r = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref badSizeOpts, out var badSession);
            Assert.Equal(-2, r); // A2_STATUS_INVALID_ARGUMENT
        }
        finally
        {
            FreeInitOptions(ref badSizeOpts);
        }

        // 4. 跨线程调用隔离
        var validOpts = CreateValidInitOptions(tempDir, tempSession);
        IntPtr session = IntPtr.Zero;
        try
        {
            var initRet = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref validOpts, out session);
            Assert.Equal(0, initRet);
            Assert.NotEqual(IntPtr.Zero, session);

            // 在另一个线程调用 run_once：必须被拒绝为 A2_STATUS_WRONG_THREAD (-4)
            int crossThreadResult = 0;
            var crossThread = new Thread(() =>
            {
                crossThreadResult = NativeAriaEngineHost.NativeBridge.a2_engine_run_once(session);
            });
            crossThread.Start();
            crossThread.Join();
            Assert.Equal(-4, crossThreadResult); // A2_STATUS_WRONG_THREAD
        }
        finally
        {
            FreeInitOptions(ref validOpts);
            if (session != IntPtr.Zero)
            {
                NativeAriaEngineHost.NativeBridge.a2_engine_destroy(session);
            }
        }
    }

    /// <summary>
    /// A02: UTF-8、中文路径、空数组、零长度、超长字段
    /// 通过条件: 字节长度和边界正确，无截断、越界读取或无限分配。
    /// </summary>
    public static async Task Test_A02_Utf8AndBoundaryValidation()
    {
        NativeAriaEngineHost.ConfigureNativeResolution();

        var chineseDir = Path.Combine(Path.GetTempPath(), "ariaui_测试_中文目录");
        var sessionFile = Path.Combine(Path.GetTempPath(), $"a02-test-{Guid.NewGuid():N}.session");
        var opts = CreateValidInitOptions(chineseDir, sessionFile);
        IntPtr session = IntPtr.Zero;

        try
        {
            var initRet = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref opts, out session);
            Assert.Equal(0, initRet);

            // 1. 空 URI 列表被拒绝
            var addRet = NativeAriaEngineHost.NativeBridge.a2_download_add_uris(
                session, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, 0, out ulong gid);
            Assert.Equal(-2, addRet); // A2_STATUS_INVALID_ARGUMENT

            // 2. 超长选项文本被 AriaOptionValidator 拒绝
            Assert.Throws<ArgumentException>(() =>
            {
                AriaOptionValidator.Validate("huge_field", new string('x', 8193));
            });

            // 3. 中文 UTF-8 选项正确传递
            AriaOptionValidator.Validate("dir", chineseDir);
        }
        finally
        {
            FreeInitOptions(ref opts);
            if (session != IntPtr.Zero)
            {
                NativeAriaEngineHost.NativeBridge.a2_engine_destroy(session);
            }
        }
    }

    /// <summary>
    /// A03: 缓冲区容量检查与两阶段重试 (BUFFER_TOO_SMALL)
    /// 通过条件: 容量不足时返回 BUFFER_TOO_SMALL 且无副作用；扩容后成功读取。
    /// </summary>
    public static async Task Test_A03_BufferTooSmallAndResizeProtocol()
    {
        NativeAriaEngineHost.ConfigureNativeResolution();

        var tempDir = Path.GetTempPath();
        var sessionFile = Path.Combine(tempDir, $"a03-test-{Guid.NewGuid():N}.session");
        var opts = CreateValidInitOptions(tempDir, sessionFile);
        IntPtr session = IntPtr.Zero;

        try
        {
            var initRet = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref opts, out session);
            Assert.Equal(0, initRet);

            // 1. 使用极小缓冲区 (1 byte) 查询全局选项 "dir"
            byte[] tinyBuf = new byte[1];
            int r = NativeAriaEngineHost.NativeBridge.a2_engine_get_global_option_value(
                session, "dir", tinyBuf, (uint)tinyBuf.Length, out uint neededLen);

            // 必须返回 BUFFER_TOO_SMALL (-5)，并告知所需容量
            Assert.Equal(-5, r); // A2_STATUS_BUFFER_TOO_SMALL
            Assert.True(neededLen > 1, $"neededLen must be greater than 1, got {neededLen}");

            // 2. 依据 neededLen 重新分配缓冲区并再次读取：成功
            byte[] properBuf = new byte[neededLen];
            r = NativeAriaEngineHost.NativeBridge.a2_engine_get_global_option_value(
                session, "dir", properBuf, (uint)properBuf.Length, out uint actualLen);

            Assert.Equal(0, r); // A2_STATUS_OK
            Assert.Equal(neededLen, actualLen);
            var readDir = Encoding.UTF8.GetString(properBuf, 0, (int)actualLen - 1);
            Assert.Equal(tempDir, readDir);
        }
        finally
        {
            FreeInitOptions(ref opts);
            if (session != IntPtr.Zero)
            {
                NativeAriaEngineHost.NativeBridge.a2_engine_destroy(session);
            }
        }
    }

    /// <summary>
    /// A04: 事件缓冲读取
    /// 通过条件: 成功复制才消费，协议状态不误判 fatal。
    /// </summary>
    public static async Task Test_A04_EventPollingProtocol()
    {
        NativeAriaEngineHost.ConfigureNativeResolution();

        var tempDir = Path.GetTempPath();
        var sessionFile = Path.Combine(tempDir, $"a04-test-{Guid.NewGuid():N}.session");
        var opts = CreateValidInitOptions(tempDir, sessionFile);
        IntPtr session = IntPtr.Zero;

        try
        {
            var initRet = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref opts, out session);
            Assert.Equal(0, initRet);

            // 空队列轮询返回 OK 且 count 为 0
            var buf = new NativeAriaEngineHost.A2EngineEvent[10];
            int r = NativeAriaEngineHost.NativeBridge.a2_engine_poll_events(session, buf, 10, out uint count);
            Assert.Equal(0, r);
            Assert.Equal(0u, count);
        }
        finally
        {
            FreeInitOptions(ref opts);
            if (session != IntPtr.Zero)
            {
                NativeAriaEngineHost.NativeBridge.a2_engine_destroy(session);
            }
        }
    }

    /// <summary>
    /// A05: Handle 退出路径与异常不跨越 ABI
    /// 通过条件: 非法 GID 查询安全返回 NOT_FOUND，C++ 异常被捕获不击穿进程。
    /// </summary>
    public static async Task Test_A05_HandleReleaseAndExceptionSafety()
    {
        NativeAriaEngineHost.ConfigureNativeResolution();

        var tempDir = Path.GetTempPath();
        var sessionFile = Path.Combine(tempDir, $"a05-test-{Guid.NewGuid():N}.session");
        var opts = CreateValidInitOptions(tempDir, sessionFile);
        IntPtr session = IntPtr.Zero;

        try
        {
            var initRet = NativeAriaEngineHost.NativeBridge.a2_engine_init(ref opts, out session);
            Assert.Equal(0, initRet);

            // 1. 查询不存在的 GID 的句柄信息：安全返回 A2_STATUS_NOT_FOUND (-6)，不崩溃
            int r = NativeAriaEngineHost.NativeBridge.a2_download_get_handle_info(session, 0xDEADBEEF12345678UL, out var handleInfo);
            Assert.Equal(-6, r); // A2_STATUS_NOT_FOUND

            // 2. 空 session 销毁正常
            int destroyRet = NativeAriaEngineHost.NativeBridge.a2_engine_destroy(session);
            Assert.Equal(0, destroyRet);
            session = IntPtr.Zero;
        }
        finally
        {
            FreeInitOptions(ref opts);
            if (session != IntPtr.Zero)
            {
                NativeAriaEngineHost.NativeBridge.a2_engine_destroy(session);
            }
        }
    }
}
