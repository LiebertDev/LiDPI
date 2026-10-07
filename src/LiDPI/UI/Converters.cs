using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LiDPI.Core;

namespace LiDPI.UI;

/// <summary>Maps an <see cref="EngineState"/> to the themed accent brush.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is EngineState s ? s switch
        {
            EngineState.Running => "Brush.AccentHorizontal",
            EngineState.Starting or EngineState.Stopping => "Brush.Amber",
            EngineState.Error => "Brush.Red",
            _ => "Brush.TextPrimary",
        } : "Brush.TextPrimary";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visibility; pass "invert" as parameter to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Log level → brush.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string)?.ToLowerInvariant() switch
        {
            "error" => "Brush.Red",
            "warn" or "warning" => "Brush.Amber",
            "debug" => "Brush.TextTertiary",
            _ => "Brush.Cyan",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><see cref="ViewModels.InlineStatusKind"/> → text brush (ok mint, error red, info amber, busy/none secondary).</summary>
public sealed class InlineStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ViewModels.InlineStatusKind k ? k switch
        {
            ViewModels.InlineStatusKind.Ok => "Brush.Mint",
            ViewModels.InlineStatusKind.Error => "Brush.Red",
            ViewModels.InlineStatusKind.Info => "Brush.Amber",
            _ => "Brush.TextSecondary",
        } : "Brush.TextSecondary";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
