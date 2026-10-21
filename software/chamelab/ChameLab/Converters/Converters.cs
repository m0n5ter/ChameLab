using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChameLab.Protocol;
using ChameLab.Transport;
using ChameLab.ViewModels;

namespace ChameLab.Converters;

/// <summary>
/// Maps an icon key (page icon, <see cref="TransportKind"/>) to the matching
/// <c>Icon.*</c> geometry from <c>Styles/Icons.axaml</c>.
/// </summary>
public sealed class IconGeometryConverter : IValueConverter
{
    public static readonly IconGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            TransportKind k => k.ToString().ToLowerInvariant(),
            string s => s,
            _ => null,
        };
        if (key is null || Application.Current is not { } app) return null;
        return app.TryGetResource("Icon." + key, app.ActualThemeVariant, out var geometry) ? geometry : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders any enum value via its friendly <c>Label()</c> extension.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public static readonly EnumLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ButtonPressFunction f => f.Label(),
        AnimationMode m => m.Label(),
        MifareClassicPrngType p => p.Label(),
        MagicGeneration g => g.Label(),
        TagSpecificType t => t.DisplayName(),
        CardSort s => s.Label(),
        DictionarySort s => s.Label(),
        null => null,
        _ => value.ToString(),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Two-way boolean negation (for the second button of a two-state switch).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>True → fully opaque, false → transparent (keeps layout stable).</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Battery percentage → sweep angle (degrees) for the gauge arc.</summary>
public sealed class PercentToSweepConverter : IValueConverter
{
    public static readonly PercentToSweepConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int pct ? Math.Clamp(pct, 0, 100) * 3.6 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Battery percentage → green / amber / red brush.</summary>
public sealed class BatteryBrushConverter : IValueConverter
{
    public static readonly BatteryBrushConverter Instance = new();
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly IBrush Fair = new SolidColorBrush(Color.FromRgb(0xF6, 0xA6, 0x23));
    private static readonly IBrush Low = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int pct when pct <= 20 => Low,
        int pct when pct <= 50 => Fair,
        _ => Good,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><c>#RRGGBB</c> string → solid brush (card colour swatches).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && Color.TryParse(s, out var c) ? new SolidColorBrush(c) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
