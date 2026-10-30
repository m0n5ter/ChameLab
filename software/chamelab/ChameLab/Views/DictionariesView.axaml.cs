using System;
using Avalonia;
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
        KnownList.LayoutUpdated += OnKnownLayoutUpdated;
    }

    private const int KnownMinRows = 3;

    // Known card keys: neither list shrinks below three rows, however tall a row is in this theme.
    private void OnKnownLayoutUpdated(object? sender, EventArgs e)
    {
        if (KnownList.ContainerFromIndex(0) is { Bounds.Height: > 0 } row)
            KnownCards.MinHeight = KnownMinRows * row.Bounds.Height + Vertical(KnownList.Padding)
                + Vertical(KnownList.BorderThickness);
        KnownKeysBox.MinHeight = KnownMinRows * KnownKeysEditor.LineHeight + Vertical(KnownKeysBox.Padding)
            + Vertical(KnownKeysBox.BorderThickness);
    }

    private static double Vertical(Thickness t) => t.Top + t.Bottom;

    private void OnListPaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Grid list && e.WidthChanged)
            list.Classes.Set("narrow", list.Bounds.Width < 520);
    }

    // Picking a swatch closes the palette flyout it lives in.
    private void OnSwatchTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}
