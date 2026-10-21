using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using ChameLab.Behaviors;

namespace ChameLab.Views;

public partial class DictionariesView : UserControl
{
    public DictionariesView()
    {
        InitializeComponent();
        // Opening dictionaries, and dragging them into place in manual order.
        _ = new ListReorder(DictList, ListScroll);
    }

    // Picking a swatch closes the palette flyout it lives in.
    private void OnSwatchTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}
