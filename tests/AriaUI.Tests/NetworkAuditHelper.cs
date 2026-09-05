using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace AriaUI.Tests;

/// <summary>
/// LIBARIA2_TEST_PLAN.md §3.4 P03: Control Port Audit Helper
/// Audits that the running process owns ZERO TCP/HTTP/WebSocket listening sockets
/// and that port 6800 (or other control ports) cannot be connected to.
/// </summary>
public static class NetworkAuditHelper
{
    public static void AssertZeroTcpListenSockets(IEnumerable<int>? allowedFixturePorts = null)
    {
        // 1. Verify default aria2 RPC port 6800 is not listening / accepting connections
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Connect("127.0.0.1", 6800);
            throw new InvalidOperationException("TCP Port 6800 is open and accepting connections, violating zero control port mandate.");
        }
        catch (SocketException)
        {
            // Expected: Connection refused
        }

        // 2. On Linux, inspect /proc/self/fd and /proc/net/tcp to verify no TCP listening sockets
        if (OperatingSystem.IsLinux())
        {
            var ownSocketInodes = new HashSet<string>();
            var fdDir = "/proc/self/fd";
            if (Directory.Exists(fdDir))
            {
                foreach (var fdFile in Directory.GetFiles(fdDir))
                {
                    try
                    {
                        var info = File.ResolveLinkTarget(fdFile, false);
                        var rawTarget = info?.LinkTarget ?? string.Empty;
                        var fullName = info?.FullName ?? string.Empty;
                        var name = info?.Name ?? string.Empty;

                        string candidate = !string.IsNullOrEmpty(rawTarget) ? rawTarget : (!string.IsNullOrEmpty(name) ? name : fullName);
                        int sIdx = candidate.IndexOf("socket:[", StringComparison.Ordinal);
                        if (sIdx >= 0)
                        {
                            int eIdx = candidate.IndexOf(']', sIdx + 8);
                            if (eIdx > sIdx + 8)
                            {
                                var inode = candidate.Substring(sIdx + 8, eIdx - (sIdx + 8));
                                ownSocketInodes.Add(inode);
                            }
                        }
                    }
                    catch { }
                }
            }

            var allowedSet = allowedFixturePorts != null ? new HashSet<int>(allowedFixturePorts) : null;
            CheckProcNetTcp("/proc/net/tcp", ownSocketInodes, allowedSet);
            CheckProcNetTcp("/proc/net/tcp6", ownSocketInodes, allowedSet);
        }
    }

    private static void CheckProcNetTcp(string procPath, HashSet<string> ownInodes, HashSet<int>? allowedPorts)
    {
        if (!File.Exists(procPath) || ownInodes.Count == 0) return;

        var lines = File.ReadAllLines(procPath);
        // Header: sl local_address rem_address st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 10)
            {
                var localAddress = parts[1];
                var state = parts[3]; // "0A" corresponds to TCP_LISTEN in Linux kernel
                var inode = parts[9];
                if (ownInodes.Contains(inode) && state == "0A")
                {
                    int port = 0;
                    int colonIdx = localAddress.IndexOf(':');
                    if (colonIdx >= 0)
                    {
                        port = Convert.ToInt32(localAddress.Substring(colonIdx + 1), 16);
                    }

                    if (allowedPorts != null && allowedPorts.Contains(port))
                    {
                        // Permitted test fixture port per LIBARIA2_TEST_PLAN.md line 103
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Process owns an unauthorized TCP listening socket on port {port} (inode {inode}, state {state}) in {procPath}. Zero control port mandate violated!");
                }
            }
        }
    }
}
