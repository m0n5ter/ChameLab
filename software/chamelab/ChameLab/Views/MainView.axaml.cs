using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ChameLab.Services;
using ChameLab.ViewModels;

namespace ChameLab.Views;

public partial class MainView : UserControl
{
    /// <summary>Below this the layout switches to phone mode (tighter margins, labels hidden).</summary>
    private const double CompactWidth = 760;
    /// <summary>Width of the docked nav pane (OpenPaneLength).</summary>
    private const double PaneWidth = 232;
    private (bool Compact, bool Overlay)? _was;
    private MainViewModel? _vm;

    public MainView()
    {
        InitializeComponent();
        // React to size so the nav pane overlays on phones and docks on desktop.
        this.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver(Adapt));
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

    private sealed class AnonymousObserver : IObserver<Rect>
    {
        private readonly Action<Rect> _onNext;
        public AnonymousObserver(Action<Rect> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Rect value) => _onNext(value);
    }
}
