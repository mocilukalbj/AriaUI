namespace AriaUI.Models;

public class AppSettings
{
    public bool AutoStartDaemon { get; set; } = true;
    public string Aria2ExecutablePath { get; set; } = string.Empty;
    public string RpcHost { get; set; } = "127.0.0.1";
    public int RpcPort { get; set; } = 6800;
    public string RpcSecret { get; set; } = "ariaui_secret_token";
    public string DefaultDownloadDir { get; set; } = string.Empty;
    public int MaxConcurrentDownloads { get; set; } = 5;
    public int MaxConnectionPerServer { get; set; } = 16;
    public int Split { get; set; } = 16;
    public long MaxOverallDownloadLimit { get; set; } = 0; // 0 = unlimited, in bytes
    public long MaxOverallUploadLimit { get; set; } = 0;   // 0 = unlimited, in bytes
    public bool EnableBtTrackers { get; set; } = true;
    public string CustomTrackersUrl { get; set; } = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt";
    public string ExtraTrackers { get; set; } = string.Empty;
    public string ThemeMode { get; set; } = "System"; // System, Dark, Light

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (RpcPort < 1024 || RpcPort > 65535)
            errors.Add("RPC 端口必须在 1024-65535 范围内");

        if (string.IsNullOrWhiteSpace(RpcHost))
            errors.Add("RPC 地址不能为空");

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

        return errors;
    }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            AutoStartDaemon = AutoStartDaemon,
            Aria2ExecutablePath = Aria2ExecutablePath,
            RpcHost = RpcHost,
            RpcPort = RpcPort,
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
            ThemeMode = ThemeMode
        };
    }
}
