using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using ChameLab.Behaviors;

namespace ChameLab.Views;

public partial class SlotsView : UserControl
{
    public SlotsView()
    {
        InitializeComponent();
        // Opening slots with a click, and dropping one onto another to swap them.
        _ = new ListReorder(SlotList, ListScroll);
    }

    // Picking a swatch closes the palette flyout it lives in.
    private void OnSwatchTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}
