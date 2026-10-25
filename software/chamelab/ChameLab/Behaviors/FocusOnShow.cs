using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behavior: <c>behaviors:FocusOnShow.IsEnabled="True"</c> focuses a text box and
/// selects its text whenever it turns visible (a name edited in place).
/// </summary>
public static class FocusOnShow
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(FocusOnShow));

    static FocusOnShow()
    {
        Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (GetIsEnabled(box) && e.NewValue is true) FocusLater(box);
        });
        // A box created already visible (a new row that starts in editing) never turns visible.
        IsEnabledProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            box.AttachedToVisualTree -= OnAttached;
            if (e.NewValue is true) box.AttachedToVisualTree += OnAttached;
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box) FocusLater(box);
    }

    private static void FocusLater(TextBox box)
    {
        // Wait for layout: a control that isn't laid out yet can't take focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsEffectivelyVisible) return;
            box.Focus();
            box.SelectAll();
            box.BringIntoView();
        }, DispatcherPriority.Background);
    }

    public static bool GetIsEnabled(TextBox element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBox element, bool value) => element.SetValue(IsEnabledProperty, value);
}
