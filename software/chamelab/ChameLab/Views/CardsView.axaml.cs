using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using ChameLab.Behaviors;

namespace ChameLab.Views;

public partial class CardsView : UserControl
{
    public CardsView()
    {
        InitializeComponent();
        MemoryScroll.LayoutUpdated += (_, _) => FitMemoryScroll();
        MemoryScroll.AddHandler(RequestBringIntoViewEvent, OnMemoryBringIntoView, handledEventsToo: true);
        // Opening cards, and dragging them into place in manual order.
        _ = new ListReorder(CardList, ListScroll);
    }

    // ---- Memory editor ---------------------------------------------------------

    /// <summary>
    /// The memory scroll has already brought the caret into view; the request must
    /// not travel on to the pane scroll, which would scroll to the caret's position
    /// as if the inner scroll weren't there (jumping up to the card details). The
    /// pane only needs to show the memory scroll itself — a no-op when it is visible.
    /// </summary>
    private void OnMemoryBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject == MemoryScroll) return;
        e.Handled = true;
        MemoryScroll.BringIntoView();
    }

    /// <summary>
    /// Caps the memory editor's own scroll area so the whole memory card (header,
    /// legend, status and the scroll area) fits in the pane once scrolled to it.
    /// </summary>
    private void FitMemoryScroll()
    {
        if (!MemoryScroll.IsEffectivelyVisible || PaneScroll.Viewport.Height <= 0) return;
        if (MemoryScroll.TranslatePoint(default, MemoryCard) is not { } top) return;
        var below = MemoryCard.Bounds.Height - top.Y - MemoryScroll.Bounds.Height;
        var max = Math.Max(160, Math.Floor(PaneScroll.Viewport.Height - top.Y - below));
        if (Math.Abs(MemoryScroll.MaxHeight - max) >= 1) MemoryScroll.MaxHeight = max;
    }

    // Picking a swatch closes the palette flyout it lives in.
    private void OnSwatchTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}
