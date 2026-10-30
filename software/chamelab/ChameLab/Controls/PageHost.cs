using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// Shows the view of <see cref="Page"/> and keeps the views of pages already visited, hidden, so
/// coming back to one only flips its visibility. Building a page's view (templates, bindings, the
/// first layout) is what made switching tabs lag on phones; each view is now built once, on the
/// first visit, and keeps its scroll position and open sections between visits.
/// </summary>
public class PageHost : Panel
{
    public static readonly StyledProperty<object?> PageProperty =
        AvaloniaProperty.Register<PageHost, object?>(nameof(Page));

    private readonly Dictionary<object, Control> _views = new();
    private Control? _shown;

    public object? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) Show(change.NewValue);
    }

    private void Show(object? page)
    {
        var view = page is null ? null : ViewFor(page);
        if (ReferenceEquals(view, _shown)) return;
        if (_shown is not null)
        {
            // A text box left focused on a hidden page would keep the keyboard up and take keys.
            var focus = TopLevel.GetTopLevel(this)?.FocusManager;
            if (focus?.GetFocusedElement() is Visual focused &&
                (ReferenceEquals(focused, _shown) || _shown.IsVisualAncestorOf(focused)))
                focus.ClearFocus();
            _shown.IsVisible = false;
        }
        _shown = view;
        if (view is not null) view.IsVisible = true;
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
}
