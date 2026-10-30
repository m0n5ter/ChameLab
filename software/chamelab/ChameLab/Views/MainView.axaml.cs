using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ChameLab.Behaviors;
using ChameLab.Services;
using ChameLab.ViewModels;

namespace ChameLab.Views;

public partial class MainView : UserControl
{
    /// <summary>Below this the layout switches to phone mode (tighter margins, labels hidden).</summary>
    private const double CompactWidth = 760;
    /// <summary>Width of the docked nav pane (OpenPaneLength).</summary>
    private const double PaneWidth = 260;
    private (bool Compact, bool Overlay)? _was;
    private MainViewModel? _vm;
    private TopLevel? _topLevel;
    private IInputPane? _inputPane;
    /// <summary>Size of the top level while the keyboard is closed.</summary>
    private Size _restSize;
    private Rect _lastBounds;

    public MainView()
    {
        InitializeComponent();
        // React to size so the nav pane overlays on phones and docks on desktop.
        this.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver(OnBoundsChanged));
        // Let view models reach the file dialogs through the hosting window.
        StorageService.TopLevelAccessor = () => TopLevel.GetTopLevel(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
        _was = null;
        Adapt(Bounds);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null) return;
        _topLevel.BackRequested += OnBackRequested;
        _inputPane = _topLevel.InputPane;
        if (_inputPane is not null) _inputPane.StateChanged += OnInputPaneChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null) _topLevel.BackRequested -= OnBackRequested;
        if (_inputPane is not null) _inputPane.StateChanged -= OnInputPaneChanged;
        _topLevel = null;
        _inputPane = null;
    }

    /// <summary>
    /// System Back (Android button or gesture, browser history, mouse back button) undoes the
    /// innermost thing first: an open drop-down, a dialog, an editor, the overlaid navigation,
    /// then the page. Only on the start page does it reach the platform, and there only on the
    /// second press, so a single stray Back doesn't close the app.
    /// </summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Handled || _topLevel is null || DataContext is not MainViewModel vm) return;
        e.Handled = CloseTopPopup(_topLevel)
                    || DialogKeys.TryCancel(_topLevel)
                    // Nothing to cancel while firmware installs, and Back must not leave the dialog either.
                    || vm.Firmware.IsDialogOpen
                    || DialogKeys.TryGoBack(_topLevel)
                    || vm.TryGoBack()
                    // Only phones and the browser tab close on an unhandled Back; desktop ignores it.
                    || (OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()) && !vm.ConfirmExit();
    }

    // ---- Card folder names, edited in place ----------------------------------

    private void OnFolderNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: CardFolderViewModel folder } || _vm is null) return;
        if (e.Key == Key.Enter) _vm.Cards.CommitRename(folder);
        else if (e.Key == Key.Escape) CardsViewModel.CancelRename(folder);
        else return;
        e.Handled = true;
    }

    // Confirming closes the flyout before the folder (and the row it opened from) goes away. The
    // folder is taken first: closing the flyout detaches its content from the row's DataContext.
    private void OnFolderDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CardFolderViewModel folder } c) return;
        if (c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout }) flyout.Hide();
        folder.DeleteCommand.Execute(null);
    }

    private void OnFolderNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CardFolderViewModel folder }) _vm?.Cards.CommitRename(folder);
    }

    /// <summary>Closes the most recently opened flyout or drop-down, if any.</summary>
    private static bool CloseTopPopup(TopLevel top)
    {
        var popups = top.OpenedPopups;
        for (var i = popups.Count - 1; i >= 0; i--)
        {
            var popup = popups[i];
            if (!popup.IsOpen || !popup.IsLightDismissEnabled) continue;
            popup.Close();
            return true;
        }
        return false;
    }

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        if (KeepClearOfKeyboard(e.NewState == InputPaneState.Open ? e.EndRect : default)) RevealFocused();
    }

    /// <summary>The focused field may now sit under the keyboard; scrolls it back up once laid out.</summary>
    private void RevealFocused()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_topLevel?.FocusManager?.GetFocusedElement() is Control focused) focused.BringIntoView();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Phones lay the on-screen keyboard over the app (always from Android 15, edge to edge),
    /// so the shell gives up the part the keyboard covers. Where the platform shrinks the app
    /// for the keyboard itself (adjustResize on older Android, the browser), that much is
    /// already clear. Returns true when there is now room taken for the keyboard.
    /// </summary>
    private bool KeepClearOfKeyboard(Rect keyboard)
    {
        var root = this.FindControl<Grid>("Root");
        if (_topLevel is null || root is null) return false;

        var covered = 0d;
        if (keyboard.Height <= 0)
        {
            _restSize = _topLevel.ClientSize;
        }
        else if (this.TransformToVisual(_topLevel) is { } toTop)
        {
            // Padding here is the system bars' safe area, already kept clear by the top level.
            var mine = new Rect(Bounds.Size).Deflate(Padding).TransformToAABB(toTop);
            var size = _topLevel.ClientSize;
            // A change of width is a rotation, not the platform making room for the keyboard.
            var shrunk = size.Width == _restSize.Width ? Math.Max(0, _restSize.Height - size.Height) : 0;
            covered = Math.Max(0, keyboard.Intersect(mine).Height - shrunk);
        }
        if (root.Margin.Bottom != covered) root.Margin = new Thickness(0, 0, 0, covered);
        Classes.Set("keyboard", covered > 0);
        return covered > 0;
    }

    /// <summary>Re-checks the keyboard overlap after a resize (rotation, or the platform's own resize).</summary>
    private void OnBoundsChanged(Rect bounds)
    {
        var shrank = bounds.Width == _lastBounds.Width && bounds.Height < _lastBounds.Height;
        _lastBounds = bounds;
        Adapt(bounds);
        if (_inputPane is not null)
            KeepClearOfKeyboard(_inputPane.State == InputPaneState.Open ? _inputPane.OccludedRect : default);
        // Likely made shorter for the keyboard by the platform itself.
        if (shrank && _topLevel?.FocusManager?.GetFocusedElement() is TextBox) RevealFocused();
    }

    // Pages differ in how much room they need, so the pane may dock or overlay on navigation.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) Adapt(Bounds);
    }

    private void Adapt(Rect bounds)
    {
        if (DataContext is not MainViewModel vm || bounds.Width <= 0) return;
        var shell = this.FindControl<SplitView>("Shell");
        if (shell is null) return;

        var compact = bounds.Width < CompactWidth;
        // The pane overlays the content once docking it would squeeze the current page.
        var overlay = compact || bounds.Width < PaneWidth + vm.CurrentPage.MinContentWidth;
        if ((compact, overlay) == _was) return;  // only react when crossing a threshold
        var overlayChanged = overlay != _was?.Overlay;
        _was = (compact, overlay);

        Classes.Set("compact", compact);
        if (!overlayChanged) return;

        vm.IsCompact = overlay;
        shell.DisplayMode = overlay ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;

        // Docked: keep the pane open; overlaying: start collapsed.
        vm.IsPaneOpen = !overlay;
    }

    private sealed class AnonymousObserver(Action<Rect> onNext) : IObserver<Rect>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Rect value) => onNext(value);
    }
}
