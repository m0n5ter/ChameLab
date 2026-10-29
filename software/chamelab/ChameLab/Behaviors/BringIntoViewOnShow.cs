using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behavior: <c>behaviors:BringIntoViewOnShow.IsEnabled="True"</c> scrolls the
/// enclosing ScrollViewer so the control is visible whenever it turns visible
/// (a progress strip, a dump result, a prompt that appears below the fold).
/// </summary>
public static class BringIntoViewOnShow
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(BringIntoViewOnShow));

    /// <summary>Scrolls an items control to a requested item after its row has been laid out.</summary>
    public static readonly AttachedProperty<object?> ItemProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, object?>("Item", typeof(BringIntoViewOnShow));

    static BringIntoViewOnShow()
    {
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (!GetIsEnabled(control) || e.NewValue is not true) return;
            BringLater(control);
        });
        ItemProperty.Changed.AddClassHandler<ItemsControl>((list, e) =>
        {
            if (e.NewValue is { } item) BringLater(list, item);
        });
    }

    private static void BringLater(Control control, object? item = null)
    {
        // Wait for layout so a newly added row has its final size and position.
        Dispatcher.UIThread.Post(() =>
        {
            if (!control.IsEffectivelyVisible) return;
            if (item is not null && control is ItemsControl list)
            {
                if (!ReferenceEquals(GetItem(list), item)) return;
                list.UpdateLayout();
                // Start the request at the list itself so its internal ScrollViewer
                // doesn't consume the request intended for the surrounding pane.
                if (list.ContainerFromItem(item) is { } row && row.TranslatePoint(default, list) is { } at)
                    list.BringIntoView(new Rect(at, row.Bounds.Size));
            }
            else control.BringIntoView();
        }, DispatcherPriority.Background);
    }

    public static object? GetItem(ItemsControl element) => element.GetValue(ItemProperty);
    public static void SetItem(ItemsControl element, object? value) => element.SetValue(ItemProperty, value);

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);
}
