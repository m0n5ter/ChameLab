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

    // ---- The open folder's title: renamed in place ----

    // A click on the open folder's title starts renaming it (Cards and Dictionaries themselves are not renamed).
    private void OnFolderTitleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ViewModels.DictionariesViewModel vm && vm.FolderTree.IsInFolder) vm.FolderTree.RenameCurrentCommand.Execute(null);
    }

    private void OnFolderTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.DictionariesViewModel vm) return;
        if (e.Key == Key.Enter) vm.FolderTree.CommitFolderRename();
        else if (e.Key == Key.Escape) vm.FolderTree.CancelFolderRename();
        else return;
        e.Handled = true;
    }

    private void OnFolderTitleLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        (DataContext as ViewModels.DictionariesViewModel)?.FolderTree.CommitFolderRename();

    // Picking a swatch (or a folder) closes the flyout it lives in.
    private void OnFlyoutPickTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}
