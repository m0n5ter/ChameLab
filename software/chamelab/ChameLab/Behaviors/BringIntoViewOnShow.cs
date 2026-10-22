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

    static BringIntoViewOnShow()
    {
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (!GetIsEnabled(control) || e.NewValue is not true) return;
            // Wait for layout so the control has its final size and position.
            Dispatcher.UIThread.Post(() =>
            {
                if (control.IsEffectivelyVisible) control.BringIntoView();
            }, DispatcherPriority.Background);
        });
    }

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);
}
