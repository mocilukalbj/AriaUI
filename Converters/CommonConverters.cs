using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using AriaUI.Helpers;

namespace AriaUI.Converters;

public class SizeConverter : IValueConverter
{
    public static readonly SizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return FormatHelper.FormatBytes(bytes);
        }
        if (value is int intBytes)
        {
            return FormatHelper.FormatBytes(intBytes);
        }
        return "0 B";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class SpeedConverter : IValueConverter
{
    public static readonly SpeedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return FormatHelper.FormatSpeed(bytes);
        }
        return "0 B/s";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusColorConverter : IValueConverter
{
    public static readonly StatusColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.ToLowerInvariant();
        return status switch
        {
            "active" => new SolidColorBrush(Color.Parse("#22c55e")),    // Green
            "waiting" => new SolidColorBrush(Color.Parse("#eab308")),   // Yellow
            "paused" => new SolidColorBrush(Color.Parse("#f97316")),    // Orange
            "complete" => new SolidColorBrush(Color.Parse("#3b82f6")),  // Blue
            "error" => new SolidColorBrush(Color.Parse("#ef4444")),     // Red
            _ => new SolidColorBrush(Color.Parse("#9ca3af"))            // Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ConnectionBrushConverter : IValueConverter
{
    public static readonly ConnectionBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isConnected && isConnected)
        {
            return new SolidColorBrush(Color.Parse("#22c55e")); // Connected Green
        }
        return new SolidColorBrush(Color.Parse("#ef4444")); // Disconnected Red
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusTextConverter : IValueConverter
{
    public static readonly StatusTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.ToLowerInvariant();
        return status switch
        {
            "active" => "正在下载",
            "waiting" => "等待中",
            "paused" => "已暂停",
            "complete" => "已完成",
            "error" => "下载失败",
            "removed" => "已取消",
            _ => status ?? "未知"
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
