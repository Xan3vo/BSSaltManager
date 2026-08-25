using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BssManager.Models;

namespace BssManager.Views;

/// <summary>
/// Status colours, kept in one place so the pill, the dot and the card's edge
/// stripe can never drift apart. These match the palette in App.xaml.
/// </summary>
internal static class StatusPalette
{
    public static readonly Color Ok = Color.FromRgb(0x2D, 0xD4, 0x8A);
    public static readonly Color Warn = Color.FromRgb(0xF0, 0xB2, 0x3C);
    public static readonly Color Danger = Color.FromRgb(0xFF, 0x5C, 0x5C);
    public static readonly Color Info = Color.FromRgb(0x5C, 0x8C, 0xFF);

    /// <summary>Idle. Light enough to stay readable on its own tinted pill.</summary>
    public static readonly Color Idle = Color.FromRgb(0x7A, 0x88, 0xA4);

    public static Color For(SessionState state) => state switch
    {
        SessionState.Active => Ok,
        SessionState.Disconnected => Warn,
        SessionState.Other => Info,
        _ => Idle
    };

    /// <summary>
    /// The mark that stands for a state. Colour alone only reads if you already
    /// know the code; a tick, a bang and a cross carry it on their own, and
    /// they survive an eye that cannot tell the red from the green.
    /// </summary>
    public static string Mark(HealthState state) => state switch
    {
        HealthState.Ok => "\uE73E",       // checkmark
        HealthState.Warning => "\uE7BA",  // warning triangle
        HealthState.Failed => "\uEA39",   // error badge
        _ => "\uE9CE"                     // question mark
    };

    public static Color For(HealthState state) => state switch
    {
        HealthState.Ok => Ok,
        HealthState.Warning => Warn,
        HealthState.Failed => Danger,
        _ => Idle
    };
}

public class HealthStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HealthState state
            ? new SolidColorBrush(StatusPalette.For(state))
            : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class SessionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionState state
            ? new SolidColorBrush(StatusPalette.For(state))
            : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Hides the fix button on checks that have no automatic repair.</summary>
public class CanFixToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Translucent version of the state colour, for the tinted background behind a
/// status pill. Keeps the pill readable without inventing a second palette.
/// </summary>
public class StateToTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value switch
        {
            SessionState s => StatusPalette.For(s),
            HealthState h => StatusPalette.For(h),
            _ => StatusPalette.Idle
        };

        var alpha = byte.TryParse(parameter as string, out var a) ? a : (byte)0x26;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The mark that goes in a health check's orb. A colour alone says which of
/// three states a blob is in only if you already know the code; a tick, a
/// bang and a cross say it without the legend, and they still read when the
/// colours are washed out by a colour-blind eye.
/// </summary>
public class HealthStateToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HealthState state ? StatusPalette.Mark(state) : StatusPalette.Mark(HealthState.Unknown);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when a string is empty.</summary>
public class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// First letter of a name, for the square tile that stands in for an avatar
/// on a card. Cheaper to scan than the full name and gives every row an
/// anchor at a fixed x position.
/// </summary>
public class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        return string.IsNullOrEmpty(text)
            ? "?"
            : text[..1].ToUpper(CultureInfo.CurrentCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
