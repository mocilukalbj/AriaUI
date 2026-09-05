using System;
using System.Collections.Generic;
using System.IO;

namespace AriaUI.Models;

public class AppSettings
{
    public string DefaultDownloadDir { get; set; } = string.Empty;
    public int MaxConcurrentDownloads { get; set; } = 5;
    public int MaxConnectionPerServer { get; set; } = 16;
    public int Split { get; set; } = 16;
    public long MaxOverallDownloadLimit { get; set; } = 0; // 0 = unlimited, in bytes
    public long MaxOverallUploadLimit { get; set; } = 0;   // 0 = unlimited, in bytes
    public bool EnableBtTrackers { get; set; } = true;
    public string CustomTrackersUrl { get; set; } = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt";
    public string ExtraTrackers { get; set; } = string.Empty;
    public bool AllowInvalidCert { get; set; } = false;
    public string ThemeMode { get; set; } = "System"; // System, Dark, Light

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (MaxConcurrentDownloads < 1 || MaxConcurrentDownloads > 64)
            errors.Add("最大并行任务数必须在 1-64 范围内");

        if (MaxConnectionPerServer < 1 || MaxConnectionPerServer > 64)
            errors.Add("单服务器最大连接数必须在 1-64 范围内");

        if (Split < 1 || Split > 64)
            errors.Add("文件分片数必须在 1-64 范围内");

        if (MaxOverallDownloadLimit < 0)
            errors.Add("下载限速不能为负数");

        if (MaxOverallUploadLimit < 0)
            errors.Add("上传限速不能为负数");

        if (!string.IsNullOrWhiteSpace(DefaultDownloadDir))
        {
            try
            {
                _ = Path.GetFullPath(DefaultDownloadDir);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add("下载目录路径格式无效");
            }
        }

        if (!string.Equals(ThemeMode, "System", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ThemeMode, "Light", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("主题模式必须为 System、Dark 或 Light");
        }

        if (!string.IsNullOrWhiteSpace(CustomTrackersUrl))
        {
            if (!Uri.TryCreate(CustomTrackersUrl, UriKind.Absolute, out var trackerUri) ||
                (trackerUri.Scheme != Uri.UriSchemeHttp && trackerUri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("自定义 Tracker 订阅 URL 必须是以 http:// 或 https:// 开头的合法地址");
            }
        }

        return errors;
    }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            DefaultDownloadDir = DefaultDownloadDir,
            MaxConcurrentDownloads = MaxConcurrentDownloads,
            MaxConnectionPerServer = MaxConnectionPerServer,
            Split = Split,
            MaxOverallDownloadLimit = MaxOverallDownloadLimit,
            MaxOverallUploadLimit = MaxOverallUploadLimit,
            EnableBtTrackers = EnableBtTrackers,
            CustomTrackersUrl = CustomTrackersUrl,
            ExtraTrackers = ExtraTrackers,
            AllowInvalidCert = AllowInvalidCert,
            ThemeMode = ThemeMode
        };
    }
}
