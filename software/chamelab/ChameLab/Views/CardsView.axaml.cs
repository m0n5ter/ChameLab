using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ChameLab.Behaviors;
using ChameLab.ViewModels;

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
        // Going back up a folder returns the list to where it was left.
        _ = new FolderScrollMemory(this, dc => (dc as CardsViewModel)?.FolderTree, ListPaneScroll, ListScroll);
        // NDEF records are dragged into place by their grip, within the editor pane.
        _ = new ListReorder(NdefList, PaneScroll);
    }

    private void OnContinuousCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control { DataContext: CardItemViewModel row } || DataContext is not CardsViewModel vm) return;
        if (vm.OpenContinuousCardCommand.CanExecute(row)) vm.OpenContinuousCardCommand.Execute(row);
        e.Handled = true;
    }

    // ---- Memory editor ---------------------------------------------------------

    /// <summary>
    /// The memory scroll has already brought the caret into view; the request must
    /// not travel on to the pane scroll, which would scroll to the caret's position
    /// as if the inner scroll weren't there (jumping up to the card details). The
    /// pane shows the part of the memory scroll where the caret now is: on a phone
    /// with the keyboard open the pane can be shorter than the memory scroll, and
    /// showing all of it would leave a caret low in it under the keyboard. Once laid
    /// out, so the inner scroll's new offset is in place.
    /// </summary>
    private void OnMemoryBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject == MemoryScroll) return;
        e.Handled = true;
        if (e.TargetObject is not { } target)
        {
            MemoryScroll.BringIntoView();
            return;
        }
        var rect = e.TargetRect;
        Dispatcher.UIThread.Post(() =>
        {
            var shown = new Rect(MemoryScroll.Bounds.Size);
            var spot = target.TransformToVisual(MemoryScroll) is { } toScroll
                ? rect.TransformToAABB(toScroll).Intersect(shown)
                : default;
            MemoryScroll.BringIntoView(spot.Height > 0 ? spot : shown);
        }, DispatcherPriority.Background);
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

    // ---- The open folder's title: renamed in place ----

    // A click on the open folder's title starts renaming it (Cards and Dictionaries themselves are not renamed).
    private void OnFolderTitleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CardsViewModel vm && vm.FolderTree.IsInFolder) vm.FolderTree.RenameCurrentCommand.Execute(null);
    }

    private void OnFolderTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CardsViewModel vm) return;
        if (e.Key == Key.Enter) vm.FolderTree.CommitFolderRename();
        else if (e.Key == Key.Escape) vm.FolderTree.CancelFolderRename();
        else return;
        e.Handled = true;
    }

    private void OnFolderTitleLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as CardsViewModel)?.FolderTree.CommitFolderRename();

    // Save keys → clipboard: one hex key per line; the page notice confirms (the menu has closed).
    private async void OnCopyKeys(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CardsViewModel vm || vm.CardKeysText() is not { } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch { return; /* clipboard unavailable (browser without permission) */ }
        vm.NotifyKeysCopied(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // The T55xx password recovered while writing → clipboard, the same way.
    private async void OnCopyWriteT55xxKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CardsViewModel vm || vm.WriteT55xxFoundKeyText() is not { } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch { return; /* clipboard unavailable (browser without permission) */ }
        vm.NotifyKeysCopied(1);
    }

    // Picking a swatch (or a folder) closes the flyout it lives in.
    // Export → clipboard: a link that opens the card in ChameLab; the page notice confirms.
    private async void OnCopyCardLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CardsViewModel vm || vm.CardLinkText() is not { } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch { return; /* clipboard unavailable (browser without permission) */ }
        vm.NotifyLinkCopied();
    }

    // Add a card → paste: a card link from the clipboard, opened like a scanned code.
    private async void OnPasteCardLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CardsViewModel vm) return;
        string? text = null;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try { text = await clipboard.TryGetTextAsync(); }
            catch { /* clipboard unavailable (browser without permission): reported as empty */ }
        }
        vm.PasteCardLink(text);
    }

    // The shared QR code closes its dialog when clicked.
    private void OnQrTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CardsViewModel vm) vm.CloseActionCommand.Execute(null);
    }

    private void OnFlyoutPickTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}
