using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// A <see cref="DictionaryRow"/> with an on/off switch on its left, for picking dictionaries:
/// a click anywhere on the row flips the switch.
/// </summary>
public class DictionaryToggle : Grid
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<DictionaryToggle, bool>(nameof(IsChecked), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> ColorProperty = DictionaryRow.ColorProperty.AddOwner<DictionaryToggle>();
    public static readonly StyledProperty<string?> TitleProperty = DictionaryRow.TitleProperty.AddOwner<DictionaryToggle>();
    public static readonly StyledProperty<int> CountProperty = DictionaryRow.CountProperty.AddOwner<DictionaryToggle>();

    private readonly ToggleSwitch _switch = new()
    {
        OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly DictionaryRow _row = new() { VerticalAlignment = VerticalAlignment.Center };

    public DictionaryToggle()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        ColumnSpacing = 4;
        // Hit-testable across the whole row, gaps included.
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        SetColumn(_row, 1);
        Children.Add(_switch);
        Children.Add(_row);
        _switch.IsCheckedChanged += (_, _) => IsChecked = _switch.IsChecked == true;
        AddHandler(TappedEvent, OnRowTapped);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
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

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty) _switch.IsChecked = IsChecked;
        else if (change.Property == ColorProperty) _row.Color = Color;
        else if (change.Property == TitleProperty) _row.Title = Title;
        else if (change.Property == CountProperty) _row.Count = Count;
    }

    // The switch flips itself; a tap elsewhere on the row flips it too.
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled || !IsEffectivelyEnabled) return;
        if (e.Source is Visual v && (ReferenceEquals(v, _switch) || _switch.IsVisualAncestorOf(v))) return;
        IsChecked = !IsChecked;
        e.Handled = true;
    }
}
