using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ChameLab.Localization;

namespace ChameLab.Controls;

/// <summary>
/// A dictionary on one compact line, for pickers and menus: a square of its color, its name
/// (sliding when too long) and the number of keys in a small frame on the right. No frame
/// around the row; the list around it highlights. Without a color (the built-in keys, the known
/// card keys) the square is neutral.
/// </summary>
public class DictionaryRow : Grid
{
    private const double SwatchSize = 12;
    private const double SwatchGap = 6;

    public static readonly StyledProperty<IBrush?> ColorProperty =
        AvaloniaProperty.Register<DictionaryRow, IBrush?>(nameof(Color));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DictionaryRow, string?>(nameof(Title));

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<DictionaryRow, int>(nameof(Count));

    private readonly Border _swatch = new()
    {
        Width = SwatchSize, Height = SwatchSize, CornerRadius = new CornerRadius(3),
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Marquee _marquee;
    private readonly TextBlock _count = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _countFrame = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

    public DictionaryRow()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        // The square sits close to the name; the count keeps more room.
        ColumnSpacing = 0;
        _swatch.Classes.Add("dictswatch");
        _countFrame.Classes.Add("dictcount");
        _countFrame.Child = _count;
        _marquee = new Marquee { Child = _title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(SwatchGap, 0, 0, 0) };
        SetColumn(_marquee, 1);
        SetColumn(_countFrame, 2);
        Children.Add(_swatch);
        Children.Add(_marquee);
        Children.Add(_countFrame);
        UpdateSwatch();
        UpdateCount();
    }

    public IBrush? Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Number of keys, shown bare in the frame (its tooltip spells it out).</summary>
    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorProperty) UpdateSwatch();
        else if (change.Property == TitleProperty) _title.Text = Title;
        else if (change.Property == CountProperty) UpdateCount();
    }

    /// <summary>
    /// Centers the square on the letters, not on the line box: the box also holds the font's
    /// ascent and descent gaps, so its middle is off the letters' and a centered square looks
    /// shifted. The letters' middle is taken a little above half the cap height over the baseline.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        var middle = finalSize.Height / 2;
        if (_title.TextLayout is { } layout)
            middle = _marquee.Bounds.Y + _title.Bounds.Y + layout.Baseline - _title.FontSize * 0.36;
        var top = Math.Clamp(Math.Round(middle - SwatchSize / 2), 0, Math.Max(0, finalSize.Height - SwatchSize));
        _swatch.Arrange(new Rect(0, top, SwatchSize, SwatchSize));
        return size;
    }

    private void UpdateSwatch()
    {
        _swatch.Classes.Set("neutral", Color is null);
        if (Color is { } color) _swatch.Background = color;
        else _swatch.ClearValue(Border.BackgroundProperty);
    }

    private void UpdateCount()
    {
        _count.Text = Count.ToString();
        ToolTip.SetTip(_countFrame, Loc.Plural(Strings.Plural_Keys, Count));
    }
}
