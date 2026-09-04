namespace AriaUI.Models;

public class AppSettings
{
    public bool AutoStartDaemon { get; set; } = true;
    public string Aria2ExecutablePath { get; set; } = string.Empty;
    public string RpcHost { get; set; } = "127.0.0.1";
    public int RpcPort { get; set; } = 6800;
    public bool RpcUseTls { get; set; }
    public string RpcSecret { get; set; } = string.Empty;
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

        if (RpcPort < 1 || RpcPort > 65535)
            errors.Add("RPC 端口必须在 1-65535 范围内");

        if (string.IsNullOrWhiteSpace(RpcHost))
            errors.Add("RPC 地址不能为空");
        else if (!IsValidRpcHost(RpcHost))
            errors.Add("RPC 地址必须是合法的主机名或 IP 地址，且不能包含协议或路径");

        if (AutoStartDaemon && RpcPort < 1024)
            errors.Add("本地托管 aria2c 的 RPC 端口必须在 1024-65535 范围内");

        if (AutoStartDaemon && !IsLoopbackHost(RpcHost))
            errors.Add("自动管理 daemon 时 RPC 地址必须是 localhost、127.0.0.1 或 ::1");

        if (AutoStartDaemon && RpcUseTls)
            errors.Add("本地托管 aria2c 未配置 RPC TLS 证书；使用 TLS 时请关闭自动管理 daemon");

        if (string.IsNullOrWhiteSpace(RpcSecret))
        {
            errors.Add("RPC 密钥不能为空");
        }
        else if (RpcSecret.Contains('\r') || RpcSecret.Contains('\n') || RpcSecret != RpcSecret.Trim())
        {
            errors.Add("RPC 密钥不能包含换行或首尾空白");
        }

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

        if (AutoStartDaemon && !string.IsNullOrWhiteSpace(Aria2ExecutablePath) && !File.Exists(Aria2ExecutablePath))
        {
            errors.Add($"指定的 aria2c 可执行文件不存在: {Aria2ExecutablePath}");
        }

        return errors;
    }

    private static bool IsLoopbackHost(string host)
    {
        var normalizedHost = host.Trim().TrimStart('[').TrimEnd(']');
        return string.Equals(normalizedHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedHost, "127.0.0.1", StringComparison.Ordinal) ||
               string.Equals(normalizedHost, "::1", StringComparison.Ordinal);
    }

    private static bool IsValidRpcHost(string host)
    {
        if (host != host.Trim())
        {
            return false;
        }

        var normalizedHost = host;
        if (host.Contains('[') || host.Contains(']'))
        {
            if (!host.StartsWith('[') || !host.EndsWith(']'))
            {
                return false;
            }

            normalizedHost = host[1..^1];
            if (normalizedHost.Contains('[') || normalizedHost.Contains(']'))
            {
                return false;
            }

            return System.Net.IPAddress.TryParse(normalizedHost, out var bracketedAddress) &&
                   bracketedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        return System.Net.IPAddress.TryParse(normalizedHost, out _) ||
               Uri.CheckHostName(normalizedHost) == UriHostNameType.Dns;
    }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            AutoStartDaemon = AutoStartDaemon,
            Aria2ExecutablePath = Aria2ExecutablePath,
            RpcHost = RpcHost,
            RpcPort = RpcPort,
            RpcUseTls = RpcUseTls,
            RpcSecret = RpcSecret,
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
