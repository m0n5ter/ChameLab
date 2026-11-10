using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChameLab.ViewModels;

namespace ChameLab.Controls;

/// <summary>The dictionary picker shared by the MIFARE recovery and T55xx panels: a checklist of
/// key sources with All/None shortcuts and the running count of unique keys.</summary>
public partial class DictionarySelector : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DictionarySelector, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> CountTextProperty =
        AvaloniaProperty.Register<DictionarySelector, string?>(nameof(CountText));

    public DictionarySelector() => InitializeComponent();

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? CountText
    {
        get => GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    private void OnAll(object? sender, RoutedEventArgs e) => SetAll(true);
    private void OnNone(object? sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool selected)
    {
        if (ItemsSource is null) return;
        foreach (var item in ItemsSource)
            if (item is DumpKeySourceViewModel source) source.IsSelected = selected;
    }
}
