using System;
using System.Globalization;
using Avalonia.Data;
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
        if (value is double dblBytes)
        {
            return FormatHelper.FormatBytes((long)dblBytes);
        }
        if (value is string str && long.TryParse(str, out var parsed))
        {
            return FormatHelper.FormatBytes(parsed);
        }
        return "0 B";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
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
        if (value is int intBytes)
        {
            return FormatHelper.FormatSpeed(intBytes);
        }
        if (value is double dblBytes)
        {
            return FormatHelper.FormatSpeed((long)dblBytes);
        }
        if (value is string str && long.TryParse(str, out var parsed))
        {
            return FormatHelper.FormatSpeed(parsed);
        }
        return "0 B/s";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public class StatusColorConverter : IValueConverter
{
    public static readonly StatusColorConverter Instance = new();

    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#22c55e"));
    private static readonly SolidColorBrush WaitingBrush = new(Color.Parse("#eab308"));
    private static readonly SolidColorBrush PausedBrush = new(Color.Parse("#f97316"));
    private static readonly SolidColorBrush CompleteBrush = new(Color.Parse("#3b82f6"));
    private static readonly SolidColorBrush ErrorBrush = new(Color.Parse("#ef4444"));
    private static readonly SolidColorBrush DefaultBrush = new(Color.Parse("#9ca3af"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.ToLowerInvariant();
        return status switch
        {
            "active" => ActiveBrush,
            "waiting" => WaitingBrush,
            "paused" => PausedBrush,
            "complete" => CompleteBrush,
            "error" => ErrorBrush,
            _ => DefaultBrush
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public class ConnectionBrushConverter : IValueConverter
{
    public static readonly ConnectionBrushConverter Instance = new();

    private static readonly SolidColorBrush ConnectedBrush = new(Color.Parse("#22c55e"));
    private static readonly SolidColorBrush DisconnectedBrush = new(Color.Parse("#ef4444"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isConnected && isConnected)
        {
            return ConnectedBrush;
        }
        return DisconnectedBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public class StatusAlertBackgroundConverter : IValueConverter
{
    public static readonly StatusAlertBackgroundConverter Instance = new();

    private static readonly SolidColorBrush DangerBg = new(Color.Parse("#fee2e2"));
    private static readonly SolidColorBrush SuccessBg = new(Color.Parse("#dcfce7"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isError && isError)
            return DangerBg;
        return SuccessBg;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public class StatusAlertBorderBrushConverter : IValueConverter
{
    public static readonly StatusAlertBorderBrushConverter Instance = new();

    private static readonly SolidColorBrush DangerBorder = new(Color.Parse("#ef4444"));
    private static readonly SolidColorBrush SuccessBorder = new(Color.Parse("#22c55e"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isError && isError)
            return DangerBorder;
        return SuccessBorder;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public class StatusAlertForegroundConverter : IValueConverter
{
    public static readonly StatusAlertForegroundConverter Instance = new();

    private static readonly SolidColorBrush DangerText = new(Color.Parse("#991b1b"));
    private static readonly SolidColorBrush SuccessText = new(Color.Parse("#166534"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isError && isError)
            return DangerText;
        return SuccessText;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
