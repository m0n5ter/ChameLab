using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behavior for a bar that takes room from the scrolling page above it (the save
/// bars): <c>behaviors:KeepFocusInView.IsEnabled="True"</c> scrolls the focused field back
/// into view whenever the bar appears or grows (a save error), since the page it shortened
/// may now hide the field being edited under the bar. A hex editor shows its caret line.
/// </summary>
public static class KeepFocusInView
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(KeepFocusInView));

    static KeepFocusInView()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((bar, e) =>
        {
            bar.PropertyChanged -= OnBarPropertyChanged;
            if (e.NewValue is true) bar.PropertyChanged += OnBarPropertyChanged;
        });
    }

    private static void OnBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Control bar) return;
        var grew = e.Property == Visual.BoundsProperty
                   && e.GetNewValue<Rect>().Height > e.GetOldValue<Rect>().Height;
        var shown = e.Property == Visual.IsVisibleProperty && e.GetNewValue<bool>();
        if (grew || shown) RevealLater(bar);
    }

    private static void RevealLater(Control bar)
    {
        // Once laid out, so the page has its new, shorter viewport.
        Dispatcher.UIThread.Post(() =>
        {
            if (!bar.IsEffectivelyVisible) return;
            if (TopLevel.GetTopLevel(bar)?.FocusManager?.GetFocusedElement() is not Control focused) return;
            // A button in the bar itself (Save just clicked) is in view already.
            if (!focused.IsEffectivelyVisible || bar.IsVisualAncestorOf(focused)) return;
            focused.BringIntoView();
        }, DispatcherPriority.Background);
    }

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);
}
