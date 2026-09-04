using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AriaUI.Services;

public class TrackerService : ITrackerService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private const int MaxTrackersFileSize = 512 * 1024; // 512 KB
    private const int MaxTrackerCount = 512;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public TrackerService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                UseProxy = false,
                ConnectCallback = ConnectToValidatedEndpointAsync
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            _ownsHttpClient = true;
        }
    }

    public async Task<List<string>> FetchTrackersAsync(string trackersUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackersUrl))
        {
            throw new ArgumentException("Tracker 订阅 URL 不能为空。", nameof(trackersUrl));
        }

        if (!Uri.TryCreate(trackersUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("非法的 Tracker 订阅 URL 地址。");
        }

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCts.CancelAfter(OperationTimeout);
        var operationToken = operationCts.Token;
        var currentUri = uri;

        for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            await ValidateUriSafeAsync(currentUri, operationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken);

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
            {
                var location = response.Headers.Location;
                if (location == null)
                {
                    throw new InvalidOperationException("收到重定向状态码但缺少 Location 头。");
                }

                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > MaxTrackersFileSize)
            {
                throw new InvalidOperationException($"Tracker 订阅文件过大 (> {MaxTrackersFileSize / 1024} KB)。");
            }

            using var stream = await response.Content.ReadAsStreamAsync(operationToken);
            using var contentBuffer = new MemoryStream();
            var buffer = new byte[4096];
            var totalRead = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(), operationToken)) > 0)
            {
                totalRead += read;
                if (totalRead > MaxTrackersFileSize)
                {
                    throw new InvalidOperationException($"Tracker 订阅响应内容超出上限 ({MaxTrackersFileSize / 1024} KB)。");
                }
                contentBuffer.Write(buffer, 0, read);
            }

            var content = StrictUtf8.GetString(
                contentBuffer.GetBuffer(),
                0,
                checked((int)contentBuffer.Length));
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }
            return await ParseAndValidateTrackersAsync(content, operationToken);
        }

        throw new InvalidOperationException("Tracker 订阅重定向次数过多。");
    }

    private static async Task<List<string>> ParseAndValidateTrackersAsync(
        string content,
        CancellationToken cancellationToken)
    {
        var trackers = new List<(string Value, Uri Uri)>();
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }

            if (value.Contains(',') || value.Any(char.IsControl) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !IsSupportedTrackerScheme(uri.Scheme) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                uri.UserInfo.Length > 0 ||
                uri.Fragment.Length > 0 ||
                (uri.Scheme == "udp" && uri.Port < 1))
            {
                throw new InvalidDataException($"Tracker 订阅包含非法地址: {value}");
            }

            trackers.Add((value, uri));
            if (trackers.Count > MaxTrackerCount)
            {
                throw new InvalidDataException($"Tracker 数量超过上限 ({MaxTrackerCount})。");
            }
        }

        if (trackers.Count == 0)
        {
            return new List<string>();
        }

        var hosts = trackers
            .Select(static tracker => tracker.Uri.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 8
            },
            async (host, token) => _ = await ResolveSafeAddressesAsync(host, token));

        return trackers
            .Select(static tracker => tracker.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsSupportedTrackerScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "udp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "wss", StringComparison.OrdinalIgnoreCase);

    private static async Task ValidateUriSafeAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Tracker 订阅仅支持 HTTP / HTTPS 协议。");
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("Tracker 订阅 URL 不能包含凭据或片段，且必须指定主机。");
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("禁止向回环地址请求 Tracker 订阅。");
        }

        _ = await ResolveSafeAddressesAsync(uri.Host, ct);
    }

    private static async ValueTask<Stream> ConnectToValidatedEndpointAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveSafeAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                if (ex is not SocketException and not IOException)
                {
                    throw;
                }

                lastError = ex;
            }
        }

        throw new HttpRequestException(
            $"Unable to connect to the validated Tracker endpoint {context.DnsEndPoint}.",
            lastError);
    }

    private static async Task<IPAddress[]> ResolveSafeAddressesAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var directIp))
        {
            addresses = new[] { directIp };
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch (SocketException ex)
            {
                throw new ArgumentException($"解析 Tracker 主机名失败: {host}", ex);
            }
        }

        if (addresses.Length == 0)
        {
            throw new ArgumentException($"无法解析 Tracker 主机名: {host}");
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrReservedAddress(address))
            {
                throw new ArgumentException(
                    $"Tracker 主机名解析到内部或保留网络地址 ({address})，请求已被拦截。");
            }
        }

        return addresses;
    }

    private static bool IsPrivateOrReservedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 100.64.0.0/10 (CGNAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 127.0.0.0/8
            if (bytes[0] == 127) return true;
            // 169.254.0.0/16 (Link-local / AWS/GCP Metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // IETF protocol assignments, documentation, and deprecated relay ranges
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) return true;
            if (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) return true;
            // 198.18.0.0/15
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;
            // TEST-NET-2 / TEST-NET-3
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;
            // 224.0.0.0/4 (Multicast) + 240.0.0.0/4 (Reserved)
            if (bytes[0] >= 224) return true;

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return true;

            var bytes = ip.GetAddressBytes();
            // Unique Local Address fc00::/7
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // Link-local fe80::/10
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
            // Discard prefix 0100::/64
            if (bytes[0] == 0x01 && bytes[1] == 0x00) return true;
            // Benchmarking, ORCHID, and documentation prefixes under 2001::/23
            if (bytes[0] == 0x20 && bytes[1] == 0x01 &&
                ((bytes[2] == 0x00 && (bytes[3] & 0xF0) is 0x10 or 0x20) ||
                 (bytes[2] == 0x00 && bytes[3] == 0x02) ||
                 (bytes[2] == 0x0D && bytes[3] == 0xB8))) return true;

            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
