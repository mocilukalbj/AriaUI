namespace AriaUI.Helpers;

public static class FormatHelper
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < SizeUnits.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {SizeUnits[order]}";
    }

    public static string FormatSpeed(long bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        return $"{FormatBytes(bytesPerSec)}/s";
    }

    public static string FormatEta(long totalBytes, long completedBytes, long speedBytesPerSec)
    {
        if (totalBytes <= 0 || speedBytesPerSec <= 0 || completedBytes >= totalBytes || completedBytes < 0) return "--";
        var remainingBytes = totalBytes - completedBytes;
        var seconds = remainingBytes / speedBytesPerSec;

        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
    }
}
