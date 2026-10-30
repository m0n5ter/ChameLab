using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// Shows the view of <see cref="Page"/> and keeps the views of pages already visited, hidden, so
/// coming back to one only flips its visibility. Building a page's view (templates, bindings, the
/// first layout) is what made switching tabs lag on phones; each view is built once and keeps its
/// scroll position and open sections between visits.
/// The views of <see cref="Preload"/> are built ahead, one per idle moment once the shown page is
/// up, and laid out unseen once, so even the first visit to a page only shows it.
/// </summary>
public class PageHost : Panel
{
    public static readonly StyledProperty<object?> PageProperty =
        AvaloniaProperty.Register<PageHost, object?>(nameof(Page));

    public static readonly StyledProperty<IEnumerable<object>?> PreloadProperty =
        AvaloniaProperty.Register<PageHost, IEnumerable<object>?>(nameof(Preload));

    private readonly Dictionary<object, Control> _views = new();
    private Control? _shown;
    /// <summary>A view laid out unseen right now, hidden again on the next idle moment.</summary>
    private Control? _warming;
    private bool _preloadQueued;

    public PageHost()
    {
        // Takes the focus off a page being left, so its text box drops the keyboard.
        Focusable = true;
    }

    public object? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public IEnumerable<object>? Preload
    {
        get => GetValue(PreloadProperty);
        set => SetValue(PreloadProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) Show(change.NewValue);
        else if (change.Property == PreloadProperty)
        {
            if (change.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= OnPreloadChanged;
            if (change.NewValue is INotifyCollectionChanged now) now.CollectionChanged += OnPreloadChanged;
            QueuePreload();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        QueuePreload();
    }

    private void OnPreloadChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueuePreload();

    private void Show(object? page)
    {
        var view = page is null ? null : ViewFor(page);
        if (ReferenceEquals(view, _shown)) return;
        if (_shown is not null)
        {
            // A text box left focused on a hidden page would keep the keyboard up and take keys.
            if (TopLevel.GetTopLevel(this)?.FocusManager.GetFocusedElement() is Visual focused &&
                (ReferenceEquals(focused, _shown) || _shown.IsVisualAncestorOf(focused)))
                Focus();
            _shown.IsVisible = false;
        }
        _shown = view;
        if (view is null) return;
        if (ReferenceEquals(view, _warming)) EndWarming();
        view.IsVisible = true;
    }

    private Control? ViewFor(object page)
    {
        if (_views.TryGetValue(page, out var view)) return view;
        view = this.FindDataTemplate(page)?.Build(page);
        if (view is null) return null;
        view.DataContext = page;
        _views[page] = view;
        Children.Add(view);
        return view;
    }

    // ---- Preloading ----------------------------------------------------

    /// <summary>
    /// One step per idle moment, so a tap in between is still answered at once: either the view
    /// laid out last time is hidden again, or the next missing view is built and laid out unseen.
    /// </summary>
    private void QueuePreload()
    {
        if (_preloadQueued || VisualRoot is null) return;
        _preloadQueued = true;
        Dispatcher.UIThread.Post(PreloadStep, DispatcherPriority.ApplicationIdle);
    }

    private void PreloadStep()
    {
        _preloadQueued = false;
        if (VisualRoot is null) return;
        if (_warming is not null)
        {
            EndWarming();
            QueuePreload();
            return;
        }
        if (Preload?.FirstOrDefault(p => !_views.ContainsKey(p)) is not { } page) return;
        if (ViewFor(page) is not { } view) return;
        if (!ReferenceEquals(view, _shown))
        {
            // Visible but see-through and out of reach of taps, until the layout pass is done.
            _warming = view;
            view.Opacity = 0;
            view.IsHitTestVisible = false;
        }
        QueuePreload();
    }

    private void EndWarming()
    {
        if (_warming is not { } view) return;
        _warming = null;
        view.Opacity = 1;
        view.IsHitTestVisible = true;
        if (!ReferenceEquals(view, _shown)) view.IsVisible = false;
    }
}
