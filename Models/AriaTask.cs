using System.Text.Json.Serialization;

namespace AriaUI.Models;

public class AriaGlobalStat
{
    [JsonPropertyName("downloadSpeed")]
    public string DownloadSpeed { get; set; } = "0";

    [JsonPropertyName("uploadSpeed")]
    public string UploadSpeed { get; set; } = "0";

    [JsonPropertyName("numActive")]
    public string NumActive { get; set; } = "0";

    [JsonPropertyName("numWaiting")]
    public string NumWaiting { get; set; } = "0";

    [JsonPropertyName("numStopped")]
    public string NumStopped { get; set; } = "0";

    [JsonPropertyName("numStoppedTotal")]
    public string NumStoppedTotal { get; set; } = "0";

    public long DownloadSpeedBytes => long.TryParse(DownloadSpeed, out var v) ? v : 0;
    public long UploadSpeedBytes => long.TryParse(UploadSpeed, out var v) ? v : 0;
    public int ActiveCount => int.TryParse(NumActive, out var v) ? v : 0;
    public int WaitingCount => int.TryParse(NumWaiting, out var v) ? v : 0;
    public int StoppedCount => int.TryParse(NumStopped, out var v) ? v : 0;
}

public class AriaTaskInfo
{
    [JsonPropertyName("gid")]
    public string Gid { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // active, waiting, paused, error, complete, removed

    [JsonPropertyName("totalLength")]
    public string TotalLength { get; set; } = "0";

    [JsonPropertyName("completedLength")]
    public string CompletedLength { get; set; } = "0";

    [JsonPropertyName("uploadLength")]
    public string UploadLength { get; set; } = "0";

    [JsonPropertyName("downloadSpeed")]
    public string DownloadSpeed { get; set; } = "0";

    [JsonPropertyName("uploadSpeed")]
    public string UploadSpeed { get; set; } = "0";

    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    [JsonPropertyName("numSeeders")]
    public string? NumSeeders { get; set; }

    [JsonPropertyName("connections")]
    public string? Connections { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("dir")]
    public string? Dir { get; set; }

    [JsonPropertyName("files")]
    public List<AriaFile>? Files { get; set; }

    [JsonPropertyName("bittorrent")]
    public AriaBittorrentInfo? Bittorrent { get; set; }

    public long TotalBytes => long.TryParse(TotalLength, out var v) ? v : 0;
    public long CompletedBytes => long.TryParse(CompletedLength, out var v) ? v : 0;
    public long DownloadSpeedBytes => long.TryParse(DownloadSpeed, out var v) ? v : 0;
    public long UploadSpeedBytes => long.TryParse(UploadSpeed, out var v) ? v : 0;

    public double ProgressPercentage => TotalBytes > 0 ? (double)CompletedBytes / TotalBytes * 100.0 : 0.0;

    public string DisplayName => Helpers.TaskNameResolver.Resolve(this);

    public string PrimaryFilePath
    {
        get
        {
            if (Files != null && Files.Count > 0 && !string.IsNullOrWhiteSpace(Files[0].Path))
                return Files[0].Path;
            return Dir ?? string.Empty;
        }
    }
}

public class AriaFile
{
    [JsonPropertyName("index")]
    public string Index { get; set; } = "1";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public string Length { get; set; } = "0";

    [JsonPropertyName("completedLength")]
    public string CompletedLength { get; set; } = "0";

    [JsonPropertyName("selected")]
    public string Selected { get; set; } = "true";

    [JsonPropertyName("uris")]
    public List<AriaUriInfo>? Uris { get; set; }
}

public class AriaUriInfo
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public class AriaBittorrentInfo
{
    [JsonPropertyName("info")]
    public AriaBittorrentMetaInfo? Info { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("creationDate")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? CreationDate { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

public class AriaBittorrentMetaInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
