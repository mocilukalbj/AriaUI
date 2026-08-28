using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AriaUI.Services;

public class TrackerService : ITrackerService
{
    private readonly HttpClient _httpClient;
    private const int MaxTrackersFileSize = 512 * 1024; // 512 KB

    public TrackerService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<List<string>> FetchTrackersAsync(string trackersUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackersUrl)) return new List<string>();

        if (!Uri.TryCreate(trackersUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("非法的 Tracker 订阅 URL 地址。");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Tracker 订阅仅支持 HTTP / HTTPS 协议。");
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("禁止向回环地址请求 Tracker 订阅。");
        }

        // SSRF defense: block link-local cloud metadata (169.254.x.x)
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.ToString().StartsWith("169.254."))
            {
                throw new ArgumentException("禁止访问指定的内部网络地址。");
            }
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > MaxTrackersFileSize)
        {
            throw new InvalidOperationException($"Tracker 订阅文件过大 (> {MaxTrackersFileSize / 1024} KB)。");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sb = new StringBuilder();
        var buffer = new char[4096];
        int totalRead = 0;
        int read;

        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxTrackersFileSize)
            {
                throw new InvalidOperationException($"Tracker 订阅响应内容超出上限 ({MaxTrackersFileSize / 1024} KB)。");
            }
            sb.Append(buffer, 0, read);
        }

        var content = sb.ToString();
        var trackers = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim())
                              .Where(t => !string.IsNullOrEmpty(t) && !t.StartsWith("#"))
                              .Where(t => t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                          t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                          t.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ||
                                          t.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                              .Distinct()
                              .ToList();

        return trackers;
    }
}
